using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ReportService(
    IDbContextFactory<WorkLensDbContext> factory,
    AiProviderOrchestrator aiProviders,
    PromptTemplateService promptTemplates,
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
        else if (HasDeterministicChange(report, body, hours, start, end))
        {
            await CapturePreviousAsync(db, report, "重產基本摘要", cancellationToken);
            report.UpdateVersion++;
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
        else if (HasDeterministicChange(report, body, hours, start, end))
        {
            await CapturePreviousAsync(db, report, "重產基本摘要", cancellationToken);
            report.UpdateVersion++;
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

    public Task<AiReportResult> GenerateWithAiAsync(
        Guid reportId,
        CancellationToken cancellationToken = default) =>
        GenerateWithAiAsync(reportId, null, cancellationToken);

    public async Task<AiReportResult> GenerateWithAiAsync(
        Guid reportId,
        Guid? promptTemplateId,
        CancellationToken cancellationToken = default,
        bool capturePrevious = true)
    {
        var preparation = await PrepareWithAiAsync(reportId, promptTemplateId, cancellationToken);
        if (!preparation.Succeeded)
        {
            return new AiReportResult(false, null, null, preparation.Error, preparation.Sanitization);
        }

        return await SendPreparedWithAiAsync(preparation.PreparedReport!, cancellationToken, capturePrevious);
    }

    public async Task<AiReportPreparationResult> PrepareWithAiAsync(
        Guid reportId,
        Guid? promptTemplateId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.SingleOrDefaultAsync(x => x.Id == reportId, cancellationToken);
        if (report is null)
        {
            return PreparationFailure("找不到報告。");
        }

        var featureSettings = await db.AiFeatureSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == AiFeatureSettings.SingletonId, cancellationToken);
        if (featureSettings?.Enabled != true)
        {
            return PreparationFailure("AI 報告整理尚未啟用，請先到設定開啟。");
        }
        var configuration = await db.AiProviders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IsDefault, cancellationToken);
        if (configuration is null)
        {
            return PreparationFailure("找不到預設 AI 設定，請先到 AI 設定指定預設組。");
        }
        var validation = await aiProviders.ValidateAsync(configuration, cancellationToken);
        if (!validation.IsValid)
        {
            return PreparationFailure(validation.Summary);
        }

        var aiProjects = await db.Projects.AsNoTracking()
            .Where(x => x.IncludeInAi && !x.IsArchived)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);
        var aiProjectIds = aiProjects.Select(x => x.Id).ToArray();
        var aiProjectNames = aiProjects.ToDictionary(x => x.Id, x => x.Name);
        var reportStartDate = DateOnly.FromDateTime(report.PeriodStart.LocalDateTime);
        var reportEndDate = DateOnly.FromDateTime(report.PeriodEnd.LocalDateTime);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(x => x.WorkDate >= reportStartDate &&
                        x.WorkDate < reportEndDate &&
                        (x.ProjectId == null || aiProjectIds.Contains(x.ProjectId.Value)))
            .ToListAsync(cancellationToken);
        entries = entries
            .OrderBy(x => x.WorkDate)
            .ThenBy(x => x.CreatedAt)
            .ToList();
        var sourceIds = await db.ActivitySources.AsNoTracking()
            .Where(x => x.IncludeInAi && x.Enabled && !x.IsArchived &&
                        (x.ProjectId == null || aiProjectIds.Contains(x.ProjectId.Value)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var allEvidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        var evidence = allEvidence
            .Where(x => ((sourceIds.Contains(x.SourceId) &&
                         (x.ProjectId == null || aiProjectIds.Contains(x.ProjectId.Value))) ||
                         (x.Kind == EvidenceKind.Manual &&
                          (x.ProjectId == null || aiProjectIds.Contains(x.ProjectId.Value)))) &&
                        x.OccurredAt >= report.PeriodStart &&
                        x.OccurredAt < report.PeriodEnd)
            .OrderBy(x => x.OccurredAt)
            .ToList();

        var input = BuildAiInput(report, entries, evidence, aiProjectNames);
        PromptTemplate? promptTemplate = null;
        try
        {
            promptTemplate = await promptTemplates.GetEffectiveAsync(promptTemplateId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Existing databases are upgraded at startup. This fallback keeps direct
            // service tests and recovery scenarios compatible during that transition.
        }
        var effectivePrompt = promptTemplate?.Content ?? ResolvePrompt(configuration, report.Kind);
        var providerTarget = configuration.ProviderType == "ask-bridge"
            ? configuration.Provider
            : configuration.Model ?? configuration.ProviderType;

        var sanitization = await aiProviders.PrepareAsync(
            new AiReportRequest(
                report.Id,
                providerTarget,
                input,
                entries.Select(x => x.Id).ToArray(),
                report.TotalHours,
                configuration.ExecutablePath,
                effectivePrompt),
            cancellationToken);
        if (!sanitization.Succeeded)
        {
            return new AiReportPreparationResult(
                null,
                sanitization.Summary,
                sanitization.Summary.Error ?? "機敏資訊檢查未完成，本次未傳送 AI。");
        }

        var preparedRequest = sanitization.PreparedRequest!;
        return new AiReportPreparationResult(
            new AiPreparedReport(
                report.Id,
                report.UpdateVersion,
                configuration,
                preparedRequest,
                promptTemplate?.Id,
                promptTemplate?.Name ?? "舊版 Prompt",
                preparedRequest.EffectivePrompt) { PreviewValues = sanitization.PreviewValues },
            sanitization.Summary,
            null);
    }

    public async Task<AiReportResult> SendPreparedWithAiAsync(
        AiPreparedReport prepared,
        CancellationToken cancellationToken = default,
        bool capturePrevious = true)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.SingleOrDefaultAsync(x => x.Id == prepared.ReportId, cancellationToken);
        if (report is null)
        {
            return new AiReportResult(false, null, null, "找不到報告。", prepared.Request.Sanitization);
        }

        var job = new AiJob
        {
            ProviderType = prepared.Configuration.ProviderType,
            Provider = prepared.Request.Target,
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow,
            PromptTemplateId = prepared.PromptTemplateId,
            PromptNameSnapshot = prepared.PromptNameSnapshot,
            PromptTextSnapshot = prepared.PromptTextSnapshot,
            SanitizationStatus = prepared.Request.Sanitization.Status.ToString(),
            SanitizedFindingCount = prepared.Request.Sanitization.TotalCount,
            SanitizedCategoriesJson = JsonSerializer.Serialize(prepared.Request.Sanitization.Notices),
            SanitizerVersion = prepared.Request.Sanitization.ScannerVersion,
            SanitizerRuleVersion = prepared.Request.Sanitization.RuleVersion
        };
        db.AiJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        var input = prepared.Request.InputMarkdown;
        var contextBytes = GetUtf8ContextByteCount(input);
        var contextSizeKb = contextBytes / 1024d;
        logger.LogInformation(
            "AI 報告上下文大小：{ContextSizeKb:F2} KB（{ContextBytes} bytes），傳送方式={Transport}，ReportId={ReportId}，Kind={ReportKind}，Period={PeriodKey}",
            contextSizeKb,
            contextBytes,
            prepared.Configuration.ProviderType == "ask-bridge" ? "檔案附件" : "HTTP JSON",
            prepared.ReportId,
            report.Kind,
            report.PeriodKey);

        var result = await aiProviders.GeneratePreparedAsync(
            prepared.Configuration,
            prepared.Request,
            cancellationToken);
        result = result with { Sanitization = prepared.Request.Sanitization };

        db.ChangeTracker.Clear();
        job = await db.AiJobs.SingleAsync(x => x.Id == job.Id, CancellationToken.None);
        report = await db.Reports.SingleOrDefaultAsync(x => x.Id == prepared.ReportId, CancellationToken.None);
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.RawResponse = result.RawResponse;
        job.Status = result.Succeeded ? "Succeeded" : "Failed";
        job.Error = result.Error;
        if (report is null)
        {
            job.Status = "Failed";
            job.Error = "摘要已被移除，未套用 AI 結果。";
            result = new AiReportResult(false, null, result.RawResponse, job.Error, result.Sanitization);
        }
        else if (result.Succeeded && report.UpdateVersion != prepared.ReportVersion)
        {
            job.Status = "Failed";
            job.Error = "摘要在 AI 整理期間已更新，未套用 AI 結果。";
            result = new AiReportResult(false, null, result.RawResponse, job.Error, result.Sanitization);
        }
        if (result.Succeeded && result.Body is not null && report is not null)
        {
            if (capturePrevious)
            {
                await CapturePreviousAsync(db, report, "AI 整理", CancellationToken.None);
            }
            report.Body = result.Body;
            report.AiJobId = job.Id;
            report.IsStale = false;
            report.GeneratedAt = DateTimeOffset.UtcNow;
            report.UpdatedAt = DateTimeOffset.UtcNow;
            report.UpdateVersion++;
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

    private static AiReportPreparationResult PreparationFailure(string error) =>
        new(
            null,
            new AiSanitizationSummary(AiSanitizationStatus.Failed, string.Empty, [], error),
            error);

    public static int GetUtf8ContextByteCount(string input) =>
        Encoding.UTF8.GetByteCount(input);

    public async Task<ReportDocument?> UpdateBodyAsync(
        Guid id,
        string body,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (report is null)
        {
            return null;
        }
        if (expectedVersion is not null && report.UpdateVersion != expectedVersion)
            throw new InvalidOperationException("摘要已在其他分頁或排程更新，請重新載入後再試。");
        if (report.Body == body) return report;
        await CapturePreviousAsync(db, report, "手動儲存", cancellationToken);
        report.Body = body;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        report.UpdateVersion++;
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    public async Task<ReportDocument> EnsureForAiAsync(
        ReportKind kind,
        DateOnly anchorDate,
        CancellationToken cancellationToken = default)
    {
        var key = kind == ReportKind.Daily
            ? anchorDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : ReportInvalidationService.GetWeeklyKey(anchorDate);
        var existing = await GetForPeriodAsync(kind, key, cancellationToken);
        if (existing is not null) return existing;
        return kind == ReportKind.Daily
            ? await GenerateDeterministicAsync(anchorDate, cancellationToken)
            : await GenerateWeeklyAsync(anchorDate, cancellationToken);
    }

    public async Task<ReportRevision?> GetPreviousRevisionAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ReportRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ReportId == reportId, cancellationToken);
    }

    public async Task<ReportDocument?> RestorePreviousAsync(
        Guid reportId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.SingleOrDefaultAsync(x => x.Id == reportId, cancellationToken);
        var previous = await db.ReportRevisions.SingleOrDefaultAsync(x => x.ReportId == reportId, cancellationToken);
        if (report is null || previous is null) return null;
        if (report.UpdateVersion != expectedVersion)
            throw new InvalidOperationException("摘要已在其他分頁或排程更新，請重新載入後再試。");
        var current = Snapshot(report, "還原前版本");
        report.Body = previous.Body;
        report.DeterministicBody = previous.DeterministicBody;
        report.TotalHours = previous.TotalHours;
        report.IsStale = true;
        report.GeneratedAt = previous.GeneratedAt;
        report.AiJobId = previous.AiJobId;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        report.UpdateVersion++;
        previous.Body = current.Body;
        previous.DeterministicBody = current.DeterministicBody;
        previous.TotalHours = current.TotalHours;
        previous.IsStale = current.IsStale;
        previous.GeneratedAt = current.GeneratedAt;
        previous.AiJobId = current.AiJobId;
        previous.SourceVersion = current.SourceVersion;
        previous.Reason = current.Reason;
        previous.CapturedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    private static bool HasDeterministicChange(
        ReportDocument report,
        string body,
        double hours,
        DateTimeOffset start,
        DateTimeOffset end) =>
        report.Body != body ||
        report.DeterministicBody != body ||
        report.TotalHours != hours ||
        report.PeriodStart != start ||
        report.PeriodEnd != end;

    private static ReportRevision Snapshot(ReportDocument report, string reason) => new()
    {
        ReportId = report.Id,
        Body = report.Body,
        DeterministicBody = report.DeterministicBody,
        TotalHours = report.TotalHours,
        IsStale = report.IsStale,
        GeneratedAt = report.GeneratedAt,
        AiJobId = report.AiJobId,
        SourceVersion = report.UpdateVersion,
        Reason = reason
    };

    private static async Task CapturePreviousAsync(
        WorkLensDbContext db,
        ReportDocument report,
        string reason,
        CancellationToken cancellationToken)
    {
        var existing = await db.ReportRevisions.SingleOrDefaultAsync(x => x.ReportId == report.Id, cancellationToken);
        if (existing is not null) db.ReportRevisions.Remove(existing);
        db.ReportRevisions.Add(Snapshot(report, reason));
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
        builder.AppendLine("工作日期,時數,標題,工作內容");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(',',
                Csv(entry.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(entry.Hours.ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(entry.Title),
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
                    builder.AppendLine($"#### {entry.WorkDate:yyyy/MM/dd}｜{entry.Hours:0.##} 小時｜{entry.Title}");
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
        var standaloneWorkItemIds = GetStandaloneWorkItemIds(activities);
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
                    AppendAzureDevOpsSummary(builder, activity, standaloneWorkItemIds);
                    AppendAzureDevOpsWorkItemActivitySummary(builder, activity);
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 目前成果");
        var currentCommits = evidence.Where(x =>
            x.Kind == EvidenceKind.Commit &&
            x.ReachabilityStatus == CommitReachabilityStatus.Current).ToList();
        var completedPullRequests = evidence.Where(x =>
            x.Kind == EvidenceKind.AzureDevOpsPullRequestClosed &&
            string.Equals(
                SourceSettingsSerializer.DeserializeAzureDevOpsMetadata(x.MetadataJson)?.Status,
                "completed",
                StringComparison.OrdinalIgnoreCase)).ToList();
        if (currentCommits.Count == 0 && completedPullRequests.Count == 0)
        {
            builder.AppendLine("- 尚無目前 branch 的自動成果證據。");
        }
        else
        {
            foreach (var commit in currentCommits)
            {
                builder.AppendLine($"- {commit.Title}");
            }

            foreach (var pullRequest in completedPullRequests)
            {
                builder.AppendLine($"- {pullRequest.Title}（PR 已完成）");
            }
        }

        return builder.ToString();
    }

    private static string BuildAiInput(
        ReportDocument report,
        IReadOnlyList<WorkEntry> entries,
        IReadOnlyList<SourceEvidence> evidence,
        IReadOnlyDictionary<Guid, string> projectNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"報告 ID：{report.Id}");
        builder.AppendLine($"確認工時：{report.TotalHours:0.##} 小時");
        builder.AppendLine();
        // DeterministicBody is intentionally not reused here. It is generated for the
        // human-facing report from every collected source and may therefore contain
        // evidence from a source or project that was later excluded from AI sharing.
        // The AI context must be rebuilt exclusively from the already-filtered inputs.
        builder.AppendLine("補充人工紀錄：");
        foreach (var entry in entries)
        {
            builder.AppendLine($"- id={entry.Id}｜專案={ProjectName(entry.ProjectId, projectNames)}｜日期={entry.WorkDate:yyyy-MM-dd}｜確認時數={entry.Hours:0.##}｜標題={entry.Title}");
            builder.AppendLine(entry.WorkContent);
        }

        builder.AppendLine("來源活動：");
        var standaloneWorkItemIds = GetStandaloneWorkItemIds(evidence);
        foreach (var item in evidence)
        {
            builder.AppendLine($"- {item.OccurredAt:O}｜{item.Kind}｜專案={ProjectName(item.ProjectId, projectNames)}｜title={item.Title}｜message={item.CommitMessage}｜branch={item.Branch}");
            AppendAzureDevOpsAiSummary(builder, item, standaloneWorkItemIds);
            AppendAzureDevOpsWorkItemActivityAiSummary(builder, item);
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
        if (activity.Kind is EvidenceKind.AzureDevOpsPullRequestCreated or EvidenceKind.AzureDevOpsPullRequestClosed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(activity.CommitMessage) ||
            string.Equals(activity.CommitMessage.Trim(), activity.Title.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        builder.AppendLine("  " + activity.CommitMessage.Trim().Replace(Environment.NewLine, Environment.NewLine + "  "));
    }

    private static void AppendAzureDevOpsSummary(StringBuilder builder, SourceEvidence activity, ISet<int> standaloneWorkItemIds)
    {
        if (activity.Kind is not (EvidenceKind.AzureDevOpsPullRequestCreated or EvidenceKind.AzureDevOpsPullRequestClosed))
        {
            return;
        }

        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsMetadata(activity.MetadataJson);
        if (metadata is null)
        {
            return;
        }

        builder.AppendLine($"  PR 狀態：{metadata.Status}；{metadata.SourceBranch} → {metadata.TargetBranch}");
        if (!string.IsNullOrWhiteSpace(metadata.Description))
        {
            builder.AppendLine("  PR 描述：" + AzureDevOpsCliService.ToPlainText(metadata.Description));
        }
        foreach (var workItem in metadata.WorkItems)
        {
            builder.AppendLine($"  Work Item #{workItem.Id} [{workItem.Type}/{workItem.State}] {workItem.Title}");
            if (standaloneWorkItemIds.Contains(workItem.Id))
            {
                builder.AppendLine("  詳細活動已由獨立 Work Item 證據提供。");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(workItem.Description))
            {
                var description = AzureDevOpsCliService.ToPlainText(workItem.Description);
                builder.AppendLine("  " + description.Replace(Environment.NewLine, Environment.NewLine + "  "));
            }

            if (!string.IsNullOrWhiteSpace(workItem.AcceptanceCriteria))
            {
                var acceptanceCriteria = AzureDevOpsCliService.ToPlainText(workItem.AcceptanceCriteria);
                builder.AppendLine("  驗收條件：" + acceptanceCriteria.Replace(Environment.NewLine, Environment.NewLine + "  "));
            }
        }
    }

    private static void AppendAzureDevOpsAiSummary(StringBuilder builder, SourceEvidence activity, ISet<int> standaloneWorkItemIds)
    {
        if (activity.Kind is not (EvidenceKind.AzureDevOpsPullRequestCreated or EvidenceKind.AzureDevOpsPullRequestClosed))
        {
            return;
        }

        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsMetadata(activity.MetadataJson);
        if (metadata is null)
        {
            return;
        }

        builder.AppendLine($"  PR：{metadata.Status}｜{metadata.SourceBranch} → {metadata.TargetBranch}｜建立者={metadata.CreatorName}");
        if (!string.IsNullOrWhiteSpace(metadata.Description))
        {
            builder.AppendLine("  PR 描述：" + AzureDevOpsCliService.ToPlainText(metadata.Description));
        }

        foreach (var workItem in metadata.WorkItems)
        {
            builder.AppendLine($"  Work Item #{workItem.Id}｜類型={workItem.Type}｜狀態={workItem.State}｜標題={workItem.Title}｜Assigned To={workItem.AssignedTo}");
            if (standaloneWorkItemIds.Contains(workItem.Id))
            {
                builder.AppendLine("  詳細活動已由獨立 Work Item 證據提供。");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(workItem.Description))
            {
                builder.AppendLine("  Description：" + AzureDevOpsCliService.ToPlainText(workItem.Description));
            }

            if (!string.IsNullOrWhiteSpace(workItem.AcceptanceCriteria))
            {
                builder.AppendLine("  Acceptance Criteria：" + AzureDevOpsCliService.ToPlainText(workItem.AcceptanceCriteria));
            }

            if (!string.IsNullOrWhiteSpace(workItem.Tags))
            {
                builder.AppendLine("  Tags：" + workItem.Tags);
            }
        }
    }

    private static HashSet<int> GetStandaloneWorkItemIds(IEnumerable<SourceEvidence> evidence) =>
        evidence.Where(item => item.Kind == EvidenceKind.AzureDevOpsWorkItemActivity)
            .Select(item => SourceSettingsSerializer.DeserializeAzureDevOpsWorkItemActivityMetadata(item.MetadataJson)?.WorkItem.Id ?? 0)
            .Where(id => id > 0)
            .ToHashSet();

    private static void AppendAzureDevOpsWorkItemActivitySummary(StringBuilder builder, SourceEvidence activity)
    {
        if (activity.Kind != EvidenceKind.AzureDevOpsWorkItemActivity) return;
        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsWorkItemActivityMetadata(activity.MetadataJson);
        if (metadata is null) return;
        builder.AppendLine($"  Work Item #{metadata.WorkItem.Id} [{metadata.WorkItem.Type}/{metadata.WorkItem.State}]");
        if (metadata.FieldChanges.Count > 0)
        {
            builder.AppendLine("  本人異動欄位：" + string.Join("、", metadata.FieldChanges.Select(change => change.Field).Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        foreach (var discussion in metadata.Discussions)
        {
            builder.AppendLine("  Discussion：" + AzureDevOpsCliService.ToPlainText(discussion.Text));
        }
    }

    private static void AppendAzureDevOpsWorkItemActivityAiSummary(StringBuilder builder, SourceEvidence activity)
    {
        if (activity.Kind != EvidenceKind.AzureDevOpsWorkItemActivity) return;
        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsWorkItemActivityMetadata(activity.MetadataJson);
        if (metadata is null) return;
        builder.AppendLine($"  Work Item：#{metadata.WorkItem.Id}｜類型={metadata.WorkItem.Type}｜狀態={metadata.WorkItem.State}｜標題={metadata.WorkItem.Title}｜Assigned To={metadata.WorkItem.AssignedTo}");
        foreach (var change in metadata.FieldChanges)
        {
            builder.AppendLine($"  異動：{change.Field}｜{AzureDevOpsCliService.ToPlainText(change.OldValue)} → {AzureDevOpsCliService.ToPlainText(change.NewValue)}");
        }
        foreach (var discussion in metadata.Discussions)
        {
            builder.AppendLine("  自己的 Discussion：" + AzureDevOpsCliService.ToPlainText(discussion.Text));
        }
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
