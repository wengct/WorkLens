using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public static class ScheduleDefaults
{
    public static readonly Guid DailyReportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid WeeklyReportId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid DailyBackupId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid WeeklyBackupId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    public const int WeekdaysMask = (1 << 1) | (1 << 2) | (1 << 3) | (1 << 4) | (1 << 5);
    public const int EveryDayMask = (1 << 7) - 1;
    public const int FridayMask = 1 << 5;

    public static IReadOnlyList<ScheduleDefinition> Create() =>
    [
        New(DailyReportId, ScheduleKind.DailyReport, WeekdaysMask, 17, 30),
        New(WeeklyReportId, ScheduleKind.WeeklyReport, FridayMask, 18, 0),
        New(DailyBackupId, ScheduleKind.DailyBackup, EveryDayMask, 17, 45)
    ];

    private static ScheduleDefinition New(Guid id, ScheduleKind kind, int days, int hour, int minute) => new()
    {
        Id = id,
        Kind = kind,
        DaysOfWeekMask = days,
        Hour = hour,
        Minute = minute
    };
}

public static class SchedulePlanner
{
    public static bool IncludesDay(int mask, DayOfWeek day) => (mask & (1 << (int)day)) != 0;

    public static bool IsDue(ScheduleDefinition schedule, DateTime localNow) =>
        schedule.Enabled &&
        IncludesDay(schedule.DaysOfWeekMask, localNow.DayOfWeek) &&
        localNow.TimeOfDay >= new TimeSpan(schedule.Hour, schedule.Minute, 0);

    public static DateTime? NextRun(ScheduleDefinition schedule, DateTime localNow)
    {
        if (!schedule.Enabled || schedule.DaysOfWeekMask == 0) return null;
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = localNow.Date.AddDays(offset);
            if (!IncludesDay(schedule.DaysOfWeekMask, date.DayOfWeek)) continue;
            var candidate = date.AddHours(schedule.Hour).AddMinutes(schedule.Minute);
            if (candidate > localNow) return candidate;
        }
        return null;
    }

    public static string PeriodKey(ScheduleKind kind, DateOnly date)
    {
        if (kind is ScheduleKind.DailyReport or ScheduleKind.DailyBackup)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var monday = date.AddDays(-(int)date.DayOfWeek + (date.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
        return $"{monday:yyyy}-W{ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue)):00}";
    }
}

public sealed record ScheduleOverview(
    ScheduleDefinition Definition,
    ScheduleExecution? LastExecution,
    DateTime? NextRun);

public sealed class ScheduleService(
    IDbContextFactory<WorkLensDbContext> factory,
    ScheduleRunner runner)
{
    public async Task<IReadOnlyList<ScheduleOverview>> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.ScheduleDefinitions.AsNoTracking()
            .Where(x => x.Kind != ScheduleKind.WeeklyBackup)
            .OrderBy(x => x.Kind)
            .ToListAsync(cancellationToken);
        var executions = (await db.ScheduleExecutions.AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(x => x.StartedAt)
            .ToList();
        var now = DateTime.Now;
        return schedules.Select(schedule => new ScheduleOverview(
            schedule,
            executions.FirstOrDefault(x => x.ScheduleId == schedule.Id),
            SchedulePlanner.NextRun(schedule, now))).ToList();
    }

    public async Task SaveAsync(ScheduleDefinition input, CancellationToken cancellationToken = default)
    {
        if (input.DaysOfWeekMask is < 1 or > 127) throw new ArgumentException("至少選擇一個執行日。");
        if (input.Hour is < 0 or > 23 || input.Minute is < 0 or > 59) throw new ArgumentException("執行時間無效。");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ScheduleDefinitions.SingleOrDefaultAsync(x => x.Id == input.Id, cancellationToken)
            ?? throw new ArgumentException("找不到排程設定。");
        if (input.PromptTemplateId is Guid promptId && promptId != existing.PromptTemplateId)
        {
            var promptIsAvailable = await db.PromptTemplates.AnyAsync(
                x => x.Id == promptId && !x.IsArchived,
                cancellationToken);
            if (!promptIsAvailable) throw new ArgumentException("指定的 Prompt 不存在或已封存。");
        }
        existing.Enabled = input.Enabled;
        existing.DaysOfWeekMask = input.DaysOfWeekMask;
        existing.Hour = input.Hour;
        existing.Minute = input.Minute;
        existing.PromptTemplateId = input.Kind is ScheduleKind.DailyReport or ScheduleKind.WeeklyReport
            ? input.PromptTemplateId
            : null;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ScheduleExecution?> RunNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
        runner.RunAsync(scheduleId, DateTime.Now, true, cancellationToken);
}

public sealed class ScheduleRunner(
    IDbContextFactory<WorkLensDbContext> factory,
    ReportService reports,
    BackupService backups,
    PromptTemplateService prompts,
    ILogger<ScheduleRunner> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public async Task RunDueAsync(DateTime localNow, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.ScheduleDefinitions.AsNoTracking()
            .Where(x => x.Enabled && x.Kind != ScheduleKind.WeeklyBackup)
            .ToListAsync(cancellationToken);
        foreach (var schedule in schedules.Where(x => SchedulePlanner.IsDue(x, localNow)))
        {
            await RunAsync(schedule.Id, localNow, false, cancellationToken);
        }
    }

    public async Task<ScheduleExecution?> RunAsync(
        Guid scheduleId,
        DateTime localNow,
        bool isManual,
        CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(scheduleId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var schedule = await db.ScheduleDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == scheduleId, cancellationToken);
            if (schedule is null) return null;

            var date = DateOnly.FromDateTime(localNow);
            var cycleKey = SchedulePlanner.PeriodKey(schedule.Kind, date);
            var periodKey = isManual ? $"manual-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}" : cycleKey;
            if (!isManual && await db.ScheduleExecutions.AnyAsync(
                    x => x.ScheduleId == scheduleId && x.PeriodKey == periodKey,
                    cancellationToken))
            {
                return null;
            }

            PromptTemplate? prompt = null;
            if (schedule.Kind is ScheduleKind.DailyReport or ScheduleKind.WeeklyReport)
            {
                prompt = await prompts.GetEffectiveAsync(schedule.PromptTemplateId, cancellationToken);
            }

            var execution = new ScheduleExecution
            {
                ScheduleId = scheduleId,
                PeriodKey = periodKey,
                IsManual = isManual,
                PromptTemplateId = prompt?.Id,
                PromptNameSnapshot = prompt?.Name ?? string.Empty,
                PromptTextSnapshot = prompt?.Content ?? string.Empty
            };
            db.ScheduleExecutions.Add(execution);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (!isManual)
            {
                return null;
            }

            try
            {
                if (schedule.Kind is ScheduleKind.DailyReport or ScheduleKind.WeeklyReport)
                {
                    execution.DeterministicStatus = "Running";
                    db.ScheduleExecutions.Update(execution);
                    await db.SaveChangesAsync(cancellationToken);
                    var report = schedule.Kind == ScheduleKind.DailyReport
                        ? await reports.GenerateDeterministicAsync(date, cancellationToken)
                        : await reports.GenerateWeeklyAsync(date, cancellationToken);
                    execution.DeterministicStatus = "Succeeded";
                    execution.AiStatus = "Running";
                    db.ScheduleExecutions.Update(execution);
                    await db.SaveChangesAsync(cancellationToken);
                    var result = await reports.GenerateWithAiAsync(report.Id, prompt!.Id, cancellationToken);
                    execution.AiStatus = result.Succeeded ? "Succeeded" : "Failed";
                    execution.Status = result.Succeeded ? "Succeeded" : "Failed";
                    execution.Error = result.Error;
                }
                else
                {
                    execution.BackupStatus = "Running";
                    db.ScheduleExecutions.Update(execution);
                    await db.SaveChangesAsync(cancellationToken);
                    var backup = await backups.CreateAsync(
                        schedule.Kind == ScheduleKind.DailyBackup ? "Daily" : "Weekly",
                        cycleKey,
                        cancellationToken);
                    execution.BackupStatus = backup is null ? "Failed" : "Succeeded";
                    execution.Status = backup is null ? "Failed" : "Succeeded";
                    execution.Error = backup is null ? "備份建立失敗；請檢查路徑、權限與系統記錄。" : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                execution.Status = "Cancelled";
                MarkRunningStage(execution, "Cancelled");
                execution.Error = "作業已取消。";
            }
            catch (Exception exception)
            {
                execution.Status = "Failed";
                MarkRunningStage(execution, "Failed");
                execution.Error = exception.Message;
                logger.LogError(exception, "排程 {ScheduleKind} 執行失敗", schedule.Kind);
            }
            finally
            {
                execution.CompletedAt = DateTimeOffset.UtcNow;
                db.ScheduleExecutions.Update(execution);
                await db.SaveChangesAsync(CancellationToken.None);
            }

            return execution;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void MarkRunningStage(ScheduleExecution execution, string status)
    {
        if (execution.DeterministicStatus == "Running") execution.DeterministicStatus = status;
        if (execution.AiStatus == "Running") execution.AiStatus = status;
        if (execution.BackupStatus == "Running") execution.BackupStatus = status;
    }
}
