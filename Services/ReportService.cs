using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ReportService(
    IDbContextFactory<WorkLensDbContext> factory,
    AskBridgeService askBridge,
    ILogger<ReportService> logger)
{
    public async Task<IReadOnlyList<ReportDocument>> GetRecentAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var reports = await db.Reports.AsNoTracking().ToListAsync(cancellationToken);
        return reports
            .OrderByDescending(x => x.PeriodStart)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToList();
    }

    public async Task<ReportDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Reports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<ReportDocument?> GetForPeriodAsync(
        ReportKind kind,
        string periodKey,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Reports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Kind == kind && x.PeriodKey == periodKey, cancellationToken);
    }

    public async Task<ReportDocument> GenerateDeterministicAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = GetLocalDayBounds(date);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(x => x.WorkDate == date)
            .ToListAsync(cancellationToken);
        entries = entries.OrderBy(x => x.CreatedAt).ToList();
        var allEvidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        var evidence = allEvidence
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end)
            .OrderBy(x => x.OccurredAt)
            .ToList();

        var hours = WorkLogService.CalculateHours(entries);
        var projects = await GetProjectNamesAsync(db, cancellationToken);
        var body = BuildBody(date.ToString("yyyy-MM-dd"), entries, evidence, hours, "每日", projects);
        var periodKey = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var report = await db.Reports.SingleOrDefaultAsync(
            x => x.Kind == ReportKind.Daily && x.PeriodKey == periodKey,
            cancellationToken);
        if (report is null)
        {
            report = new ReportDocument
            {
                Kind = ReportKind.Daily,
                PeriodKey = periodKey,
                PeriodStart = start,
                PeriodEnd = end
            };
            db.Reports.Add(report);
        }

        report.TotalHours = hours;
        report.DeterministicBody = body;
        report.Body = body;
        report.IsStale = false;
        report.GeneratedAt = DateTimeOffset.UtcNow;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    public async Task<ReportDocument> GenerateWeeklyAsync(
        DateOnly anchorDate,
        CancellationToken cancellationToken = default)
    {
        var monday = anchorDate.AddDays(-(int)anchorDate.DayOfWeek + (anchorDate.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
        var (start, _) = GetLocalDayBounds(monday);
        var (_, end) = GetLocalDayBounds(monday.AddDays(7));
        var weekEnd = monday.AddDays(7);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(x => x.WorkDate >= monday && x.WorkDate < weekEnd)
            .ToListAsync(cancellationToken);
        entries = entries
            .OrderBy(x => x.WorkDate)
            .ThenBy(x => x.CreatedAt)
            .ToList();
        var allEvidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        var evidence = allEvidence
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end)
            .OrderBy(x => x.OccurredAt)
            .ToList();
        var hours = WorkLogService.CalculateHours(entries);
        var periodKey = $"{monday:yyyy}-W{ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue)):00}";
        var projects = await GetProjectNamesAsync(db, cancellationToken);
        var body = BuildBody(periodKey, entries, evidence, hours, "每週", projects);
        var report = await db.Reports.SingleOrDefaultAsync(
            x => x.Kind == ReportKind.Weekly && x.PeriodKey == periodKey,
            cancellationToken);
        if (report is null)
        {
            report = new ReportDocument
            {
                Kind = ReportKind.Weekly,
                PeriodKey = periodKey,
                PeriodStart = start,
                PeriodEnd = end
            };
            db.Reports.Add(report);
        }

        report.TotalHours = hours;
        report.DeterministicBody = body;
        report.Body = body;
        report.IsStale = false;
        report.GeneratedAt = DateTimeOffset.UtcNow;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    public async Task<AiReportResult> GenerateWithAiAsync(
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.SingleOrDefaultAsync(x => x.Id == reportId, cancellationToken);
        if (report is null)
        {
            return new AiReportResult(false, null, null, "找不到報告。");
        }

        var configuration = await db.AiProviders.SingleAsync(cancellationToken);
        if (!configuration.Enabled)
        {
            return new AiReportResult(false, null, null, "AI 報告整理尚未啟用，請先到設定開啟。");
        }
        var detection = await askBridge.DetectAsync(configuration, cancellationToken);
        if (!detection.Validation.IsValid || detection.ExecutablePath is null)
        {
            return new AiReportResult(false, null, null, detection.Validation.Summary);
        }

        var aiProjects = await db.Projects.AsNoTracking()
            .Where(x => x.IncludeInAi && !x.IsArchived)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var reportStartDate = DateOnly.FromDateTime(report.PeriodStart.LocalDateTime);
        var reportEndDate = DateOnly.FromDateTime(report.PeriodEnd.LocalDateTime);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(x => x.WorkDate >= reportStartDate &&
                        x.WorkDate < reportEndDate &&
                        (x.ProjectId == null || aiProjects.Contains(x.ProjectId.Value)))
            .ToListAsync(cancellationToken);
        entries = entries
            .OrderBy(x => x.WorkDate)
            .ThenBy(x => x.CreatedAt)
            .ToList();
        var sourceIds = await db.ActivitySources.AsNoTracking()
            .Where(x => x.IncludeInAi && x.Enabled && !x.IsArchived &&
                        (x.ProjectId == null || aiProjects.Contains(x.ProjectId.Value)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var allEvidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        var evidence = allEvidence
            .Where(x => sourceIds.Contains(x.SourceId) &&
                        x.OccurredAt >= report.PeriodStart &&
                        x.OccurredAt < report.PeriodEnd)
            .OrderBy(x => x.OccurredAt)
            .ToList();

        var input = BuildAiInput(report, entries, evidence);
        var effectivePrompt = ResolvePrompt(configuration, report.Kind);
        var job = new AiJob
        {
            Provider = configuration.Provider,
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow
        };
        db.AiJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        var contextBytes = GetUtf8ContextByteCount(input);
        var contextSizeKb = contextBytes / 1024d;
        logger.LogInformation(
            "AI 報告上下文大小：{ContextSizeKb:F2} KB（{ContextBytes} bytes），傳送方式=檔案附件，ReportId={ReportId}，Kind={ReportKind}，Period={PeriodKey}",
            contextSizeKb,
            contextBytes,
            report.Id,
            report.Kind,
            report.PeriodKey);

        var result = await askBridge.GenerateAsync(
            configuration,
            new AiReportRequest(
                report.Id,
                configuration.Provider,
                input,
                entries.Select(x => x.Id).ToArray(),
                report.TotalHours,
                configuration.ExecutablePath,
                effectivePrompt),
            cancellationToken);

        job.CompletedAt = DateTimeOffset.UtcNow;
        job.RawResponse = result.RawResponse;
        job.Status = result.Succeeded ? "Succeeded" : "Failed";
        job.Error = result.Error;
        if (result.Succeeded && result.Body is not null)
        {
            report.Body = result.Body;
            report.AiJobId = job.Id;
            report.IsStale = false;
            report.GeneratedAt = DateTimeOffset.UtcNow;
            report.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // The provider runner converts cancellation into a failed result. Persist that terminal
        // state even though the UI cancellation token has already been cancelled, otherwise the
        // job remains permanently marked as Running.
        await db.SaveChangesAsync(CancellationToken.None);
        if (!result.Succeeded)
        {
            logger.LogWarning("AI 報告產生失敗：{Error}", result.Error);
        }

        return result;
    }

    public static int GetUtf8ContextByteCount(string input) =>
        Encoding.UTF8.GetByteCount(input);

    public async Task UpdateBodyAsync(Guid id, string body, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (report is null)
        {
            return;
        }

        report.Body = body;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> ExportCsvAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (report is null)
        {
            return null;
        }

        var reportStartDate = DateOnly.FromDateTime(report.PeriodStart.LocalDateTime);
        var reportEndDate = DateOnly.FromDateTime(report.PeriodEnd.LocalDateTime);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(x => x.WorkDate >= reportStartDate && x.WorkDate < reportEndDate)
            .ToListAsync(cancellationToken);
        entries = entries
            .OrderBy(x => x.WorkDate)
            .ThenBy(x => x.CreatedAt)
            .ToList();
        var builder = new StringBuilder();
        builder.AppendLine("工作日期,時數,工作內容");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(',',
                Csv(entry.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(entry.Hours.ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(entry.WorkContent)));
        }

        return builder.ToString();
    }

    private static string BuildBody(
        string period,
        IReadOnlyList<WorkEntry> entries,
        IReadOnlyList<SourceEvidence> evidence,
        double totalHours,
        string label,
        IReadOnlyDictionary<Guid, string> projectNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {label}工作回報 {period}");
        builder.AppendLine();
        builder.AppendLine($"今日／本週總工時：{totalHours:0.##} 小時");
        builder.AppendLine();
        builder.AppendLine("## 工作項目");
        if (entries.Count == 0)
        {
            builder.AppendLine("- 尚未填寫人工工作紀錄。");
        }
        else
        {
            foreach (var group in entries.GroupBy(entry => ProjectName(entry.ProjectId, projectNames)))
            {
                builder.AppendLine($"### {group.Key}");
                foreach (var entry in group)
                {
                    builder.AppendLine($"#### {entry.WorkDate:yyyy/MM/dd}｜{entry.Hours:0.##} 小時");
                    builder.AppendLine(entry.WorkContent);
                    builder.AppendLine();
                }
                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 實作過程與歷程");
        var activities = evidence
            .Where(x => x.Kind != EvidenceKind.WorkingTreeSnapshot)
            .OrderBy(x => x.OccurredAt)
            .ToList();
        if (activities.Count == 0)
        {
            builder.AppendLine("- 尚未收集自動活動；請以人工紀錄補充工作過程。");
        }
        else
        {
            foreach (var group in activities.GroupBy(activity => ProjectName(activity.ProjectId, projectNames)))
            {
                builder.AppendLine($"### {group.Key}");
                foreach (var activity in group)
                {
                    var status = activity.ReachabilityStatus == CommitReachabilityStatus.Superseded
                        ? "（後續被重寫，歷程保留）"
                        : string.Empty;
                    builder.AppendLine($"- {activity.OccurredAt:HH:mm} [{activity.Kind}] {activity.Title} {status}");
                    AppendCommitMessage(builder, activity);
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 目前成果");
        var currentCommits = evidence.Where(x =>
            x.Kind == EvidenceKind.Commit &&
            x.ReachabilityStatus == CommitReachabilityStatus.Current).ToList();
        if (currentCommits.Count == 0)
        {
            builder.AppendLine("- 尚無目前 branch 的自動成果證據。");
        }
        else
        {
            foreach (var commit in currentCommits)
            {
                builder.AppendLine($"- {commit.Title}");
            }
        }

        return builder.ToString();
    }

    private static string BuildAiInput(
        ReportDocument report,
        IReadOnlyList<WorkEntry> entries,
        IReadOnlyList<SourceEvidence> evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"報告 ID：{report.Id}");
        builder.AppendLine($"確認工時：{report.TotalHours:0.##} 小時");
        builder.AppendLine();
        builder.AppendLine(report.DeterministicBody);
        builder.AppendLine();
        builder.AppendLine("補充人工紀錄：");
        foreach (var entry in entries)
        {
            builder.AppendLine($"- id={entry.Id}｜日期={entry.WorkDate:yyyy-MM-dd}｜確認時數={entry.Hours:0.##}");
            builder.AppendLine(entry.WorkContent);
        }

        builder.AppendLine("來源活動：");
        foreach (var item in evidence)
        {
            builder.AppendLine($"- {item.OccurredAt:O}｜{item.Kind}｜title={item.Title}｜message={item.CommitMessage}｜branch={item.Branch}");
        }

        return builder.ToString();
    }

    public static string ResolvePrompt(AiProviderConfiguration configuration, ReportKind kind)
    {
        var overridePrompt = kind == ReportKind.Daily
            ? configuration.DailyReportPromptOverride
            : configuration.WeeklyReportPromptOverride;
        return string.IsNullOrWhiteSpace(overridePrompt)
            ? configuration.GeneralReportPrompt
            : overridePrompt.Trim();
    }

    private static string ProjectName(Guid? projectId, IReadOnlyDictionary<Guid, string> projectNames) =>
        projectId is Guid id && projectNames.TryGetValue(id, out var name) ? name : "未分類";

    private static async Task<IReadOnlyDictionary<Guid, string>> GetProjectNamesAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        return await db.Projects.AsNoTracking().ToDictionaryAsync(project => project.Id, project => project.Name, cancellationToken);
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ")}\"";

    private static void AppendCommitMessage(StringBuilder builder, SourceEvidence activity)
    {
        if (string.IsNullOrWhiteSpace(activity.CommitMessage) ||
            string.Equals(activity.CommitMessage.Trim(), activity.Title.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        builder.AppendLine("  " + activity.CommitMessage.Trim().Replace(Environment.NewLine, Environment.NewLine + "  "));
    }

    private static (DateTimeOffset Start, DateTimeOffset End) GetLocalDayBounds(DateOnly date)
    {
        var zone = TimeZoneInfo.Local;
        var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        return (
            new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)),
            new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)));
    }
}
