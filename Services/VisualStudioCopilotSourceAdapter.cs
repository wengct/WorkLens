using System.Text;
using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

/// <summary>
/// Reads Visual Studio 2026 native Copilot Chat session files. The files are
/// concatenated MessagePack values rather than JSON documents.
/// </summary>
public sealed class VisualStudioCopilotSourceAdapter : IActivitySourceAdapter
{
    private const string RepositoryKey = "visual-studio-copilot-session";
    private const string SourceFormat = "visual-studio-copilot-msgpack";
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    public string SourceType => ActivitySourceType.WindowsVisualStudioCopilot.ToString();
    public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

    public Task<SourceValidationResult> ValidateAsync(
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Visual Studio Copilot Chat 來源目前只支援 Windows。"));
        }

        var settings = SourceSettingsSerializer.DeserializeVisualStudioCopilot(source.SettingsJson);
        List<string> paths;
        try
        {
            paths = NormalizePaths(settings.SolutionPaths);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Visual Studio 方案目錄設定無法解析。",
                exception.Message));
        }

        if (paths.Count == 0)
        {
            return Task.FromResult(SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Visual Studio Copilot Chat 來源至少需要一個方案目錄。"));
        }

        var pathStatuses = paths
            .Select(path => (Path: path, Status: SourcePathProbe.CheckDirectory(path)))
            .ToList();
        var existing = pathStatuses
            .Where(item => item.Status == SourcePathStatus.Readable)
            .Select(item => item.Path)
            .ToList();
        if (existing.Count == 0)
        {
            var unavailableSummary = pathStatuses.Any(item => item.Status == SourcePathStatus.AccessDenied)
                ? "Visual Studio 方案目錄存取遭拒。"
                : pathStatuses.Any(item => item.Status == SourcePathStatus.Unreadable)
                    ? "Visual Studio 方案目錄無法讀取。"
                    : "找不到任何 Visual Studio 方案目錄。";
            return Task.FromResult(SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                unavailableSummary,
                pathStatuses.Select(item => SourcePathProbe.Describe(item.Path, item.Status)).ToArray()));
        }

        var details = pathStatuses
            .Select(item => item.Status == SourcePathStatus.Readable
                ? $"方案目錄：{item.Path}"
                : SourcePathProbe.Describe(item.Path, item.Status))
            .ToList();
        var diagnostics = new List<string>();
        var parseableSessionCount = 0;
        var sessionFiles = new List<(FileInfo File, string SolutionPath)>();
        foreach (var path in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                sessionFiles.AddRange(EnumerateSessionFiles(path).Select(file => (file, path)));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.Add($"{path}：無法列出 Visual Studio Copilot Chat session：{exception.Message}");
            }
        }

        if (sessionFiles.Count == 0)
        {
            details.Add("方案目錄可讀取，但目前沒有找到 Visual Studio Copilot Chat session 檔案。");
        }
        else
        {
            var sessionsWithUserText = 0;
            foreach (var (file, solutionPath) in sessionFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = ParseSession(file, solutionPath, diagnostics);
                if (parsed is null)
                {
                    continue;
                }

                parseableSessionCount++;
                if (parsed.Messages.Any(message => message.Role == "user"))
                {
                    sessionsWithUserText++;
                }
            }

            details.Add($"找到 {sessionFiles.Count} 個 session 檔案；可解析 {parseableSessionCount} 個，其中 {sessionsWithUserText} 個包含使用者提示詞。");
            if (parseableSessionCount == 0)
            {
                details.Add("沒有任何 session 能以 Visual Studio 2026 MessagePack 格式解析。");
            }
        }

        details.AddRange(diagnostics);
        var hasPathErrors = pathStatuses.Any(item => item.Status != SourcePathStatus.Readable);
        var hasParseableSession = sessionFiles.Count == 0 || parseableSessionCount > 0;
        if (sessionFiles.Count > 0 && !hasParseableSession)
        {
            return Task.FromResult(SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Visual Studio Copilot Chat session 無法解析。",
                details.ToArray()));
        }

        return Task.FromResult(SourceValidationResult.Valid(
            hasPathErrors || diagnostics.Count > 0
                ? "Visual Studio 2026 Copilot Chat 可讀取，但部分資料需要重試。"
                : sessionFiles.Count == 0
                    ? "Visual Studio 方案目錄可讀取，目前沒有對話檔案。"
                    : "Visual Studio 2026 Copilot Chat 對話資料可讀取。",
            details.ToArray()));
    }

    public Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        if (!OperatingSystem.IsWindows())
        {
            batch.Warnings.Add("Visual Studio Copilot Chat 來源目前只支援 Windows。");
            return Task.FromResult(batch);
        }

        var settings = SourceSettingsSerializer.DeserializeVisualStudioCopilot(request.Source.SettingsJson);
        List<string> paths;
        try
        {
            paths = NormalizePaths(settings.SolutionPaths);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            batch.Warnings.Add($"Visual Studio 方案目錄設定無法解析：{exception.Message}");
            return Task.FromResult(batch);
        }

        var scanStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var allFiles = new List<(FileInfo File, string SolutionPath)>();
            var readableRootCount = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = SourcePathProbe.CheckDirectory(path);
                if (status != SourcePathStatus.Readable)
                {
                    batch.Warnings.Add(SourcePathProbe.Describe(path, status));
                    continue;
                }

                readableRootCount++;
                try
                {
                    allFiles.AddRange(EnumerateSessionFiles(path).Select(file => (file, path)));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    batch.Warnings.Add($"{path}：無法列出 Visual Studio Copilot Chat session：{exception.Message}");
                }
            }

            var checkpoint = DeserializeCheckpoint(request.Source.CheckpointJson);
            var isBackfill = !request.UpdateCheckpoint;
            var isInitialImport = request.UpdateCheckpoint && request.Source.LastSuccessAt is null;
            var watermark = (checkpoint.LastScanAt ?? request.Since).Subtract(Overlap);
            var retryPaths = checkpoint.RetryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = isBackfill
                ? allFiles
                : allFiles.Where(item =>
                        item.File.LastWriteTimeUtc >= watermark.UtcDateTime ||
                        retryPaths.Contains(item.File.FullName))
                    .ToList();
            var nextRetryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sessions = new Dictionary<string, ParsedSession>(StringComparer.OrdinalIgnoreCase);

            foreach (var (file, solutionPath) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = ParseSession(file, solutionPath, batch.Warnings);
                if (parsed is null)
                {
                    continue;
                }

                if (!parsed.IsComplete)
                {
                    nextRetryPaths.Add(file.FullName);
                }

                if (isInitialImport && parsed.OccurredAt < request.Since)
                {
                    continue;
                }

                if (isBackfill &&
                    (parsed.OccurredAt < request.Since ||
                     (request.Until is not null && parsed.OccurredAt >= request.Until.Value)))
                {
                    continue;
                }

                var identity = $"{parsed.SourceFormat}:{CopilotSourceIdentity.Fingerprint(solutionPath)}:{parsed.SessionId}";
                if (!sessions.TryGetValue(identity, out var current) ||
                    parsed.UpdatedAt > current.UpdatedAt ||
                    (parsed.UpdatedAt == current.UpdatedAt && parsed.IsComplete && !current.IsComplete))
                {
                    sessions[identity] = parsed;
                }
            }

            foreach (var session in sessions.Values.OrderBy(item => item.OccurredAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var userContext = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    session.Messages
                        .Where(message => message.Role == "user")
                        .Select(message => message.Text.Trim())
                        .Where(text => text.Length > 0));
                if (string.IsNullOrWhiteSpace(userContext))
                {
                    continue;
                }

                var metadata = new CopilotSessionMetadata
                {
                    SessionId = session.SessionId,
                    UpdatedAt = session.UpdatedAt,
                    Cwd = session.SolutionPath,
                    Platform = ActivitySourceType.WindowsVisualStudioCopilot.ToString(),
                    SourceFormat = session.SourceFormat,
                    Client = session.Client,
                    SourcePath = session.FilePath,
                    IsComplete = session.IsComplete,
                    Messages = session.Messages
                };
                batch.Evidence.Add(new SourceEvidence
                {
                    SourceId = request.Source.Id,
                    ProjectId = request.Source.ProjectId,
                    RepositoryKey = RepositoryKey,
                    RepositoryPath = session.SolutionPath,
                    Environment = ActivitySourceType.WindowsVisualStudioCopilot.ToString(),
                    Kind = EvidenceKind.CopilotSession,
                    ExternalKey = $"session:{session.SourceFormat}:{CopilotSourceIdentity.Fingerprint(session.SolutionPath)}:{session.SessionId}",
                    Title = BuildTitle(session.Messages.FirstOrDefault(message => message.Role == "user")?.Text ?? session.SessionId),
                    CommitMessage = userContext,
                    OccurredAt = session.OccurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeCopilotMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                });
            }

            batch.SuccessfulRepositories = readableRootCount > 0 ? 1 : 0;
            if (request.UpdateCheckpoint)
            {
                batch.CheckpointJson = JsonSerializer.Serialize(new VisualStudioCheckpoint
                {
                    LastScanAt = scanStartedAt,
                    RetryPaths = nextRetryPaths.ToList()
                });
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            batch.Warnings.Add(exception.Message);
        }

        return Task.FromResult(batch);
    }

    private static IReadOnlyList<FileInfo> EnumerateSessionFiles(string solutionPath)
    {
        var visualStudioDirectory = Path.Combine(solutionPath, ".vs");
        if (!Directory.Exists(visualStudioDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(
                visualStudioDirectory,
                "*",
                SearchOption.AllDirectories)
            .Where(path => path.Contains(
                    $"{Path.DirectorySeparatorChar}copilot-chat{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) &&
                path.Contains(
                    $"{Path.DirectorySeparatorChar}sessions{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .ToList();
    }

    private static ParsedSession? ParseSession(
        FileInfo file,
        string solutionPath,
        ICollection<string> warnings)
    {
        file.Refresh();
        var initialLength = file.Length;
        var initialWriteTime = file.LastWriteTimeUtc;
        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add($"{file.Name}：無法讀取：{exception.Message}");
            return null;
        }

        var parse = VisualStudioMessagePackParser.Parse(bytes);
        if (HasFileChanged(file, initialLength, initialWriteTime))
        {
            parse.IsComplete = false;
            parse.Diagnostics.Add("檔案在讀取期間變動，保留目前快照並安排重試。");
        }

        if (parse.Root is null)
        {
            warnings.Add($"{file.Name}：找不到 Visual Studio Copilot session 根物件，已略過。");
            return null;
        }

        var sessionId = VisualStudioMessagePackParser.GetSessionId(parse.Root) ??
                        Path.GetFileName(file.FullName);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            warnings.Add($"{file.Name}：找不到有效的 session id，已略過。");
            return null;
        }

        var createdAt = VisualStudioMessagePackParser.GetTimestamp(parse.Root, "TimeCreated");
        var updatedAt = VisualStudioMessagePackParser.GetTimestamp(parse.Root, "TimeUpdated");
        if (createdAt is null)
        {
            warnings.Add($"{file.Name}：找不到 TimeCreated，已略過。");
            return null;
        }

        var diagnosticCountBeforeMessages = parse.Diagnostics.Count;
        var messages = VisualStudioMessagePackParser.ExtractMessages(
                parse.Values,
                createdAt.Value,
                parse.Diagnostics)
            .GroupBy(message => message.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(message => message.Text.Length)
                .First())
            .OrderBy(message => message.Timestamp)
            .ToList();
        if (parse.Diagnostics.Count > diagnosticCountBeforeMessages)
        {
            parse.IsComplete = false;
        }
        if (!parse.IsComplete)
        {
            warnings.Add($"{file.Name}：MessagePack session 尚未完整讀取，已保留可讀部分並安排重試。");
        }
        foreach (var diagnostic in parse.Diagnostics)
        {
            warnings.Add($"{file.Name}：{diagnostic}");
        }
        // TimeUpdated is part of the native session snapshot and remains stable
        // when a session file is copied for backup or history import. Fall back
        // to the file timestamp only for older snapshots that omit it.
        var finalUpdatedAt = updatedAt ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var client = VisualStudioMessagePackParser.IsAgentPreview(parse.Root)
            ? "GitHub Copilot Agent (Preview)"
            : "GitHub Copilot Chat";
        return new ParsedSession(
            sessionId.Trim(),
            createdAt.Value,
            finalUpdatedAt,
            solutionPath,
            file.FullName,
            parse.IsComplete,
            messages,
            client,
            SourceFormat);
    }

    private static bool HasFileChanged(
        FileInfo file,
        long initialLength,
        DateTime initialWriteTime)
    {
        file.Refresh();
        return file.Length != initialLength || file.LastWriteTimeUtc != initialWriteTime;
    }

    private static string BuildTitle(string text)
    {
        var builder = new StringBuilder(text.Trim().Length);
        var previousWasWhitespace = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }
                previousWasWhitespace = true;
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        var title = builder.ToString();
        return title.Length <= 100 ? title : title[..99] + "…";
    }

    private static VisualStudioCheckpoint DeserializeCheckpoint(string? json)
    {
        try
        {
            var checkpoint = string.IsNullOrWhiteSpace(json)
                ? new VisualStudioCheckpoint()
                : JsonSerializer.Deserialize<VisualStudioCheckpoint>(json) ?? new VisualStudioCheckpoint();
            checkpoint.RetryPaths ??= [];
            return checkpoint;
        }
        catch (JsonException)
        {
            return new VisualStudioCheckpoint();
        }
    }

    private sealed class VisualStudioCheckpoint
    {
        public DateTimeOffset? LastScanAt { get; set; }
        public List<string> RetryPaths { get; set; } = [];
    }

    private sealed record ParsedSession(
        string SessionId,
        DateTimeOffset OccurredAt,
        DateTimeOffset UpdatedAt,
        string SolutionPath,
        string FilePath,
        bool IsComplete,
        List<CopilotSessionMessage> Messages,
        string Client,
        string SourceFormat);

    private static List<string> NormalizePaths(IEnumerable<string>? paths) =>
        (paths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
