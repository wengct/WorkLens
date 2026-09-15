using System.Globalization;
using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AntigravityCliSourceAdapter : IActivitySourceAdapter
{
    private const string RepositoryKey = "antigravity-cli-session";
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private readonly IAntigravityCliHomeResolver homeResolver;
    private readonly ActivitySourceType sourceType;

    public AntigravityCliSourceAdapter(ProcessRunner processRunner, ActivitySourceType sourceType)
        : this(new AntigravityCliHomeResolver(processRunner), sourceType)
    {
    }

    public AntigravityCliSourceAdapter(IAntigravityCliHomeResolver homeResolver, ActivitySourceType sourceType)
    {
        this.homeResolver = homeResolver;
        this.sourceType = sourceType;
    }

    public string SourceType => sourceType.ToString();
    public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

    public async Task<SourceValidationResult> ValidateAsync(
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        if (sourceType == ActivitySourceType.MacOsAntigravityCli && !OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "macOS Antigravity CLI 來源只能在 macOS 上執行。");
        }

        if (sourceType == ActivitySourceType.WindowsAntigravityCli && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "Windows Antigravity CLI 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslAntigravityCli && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "WSL Antigravity CLI 來源只能在 Windows 上執行。");
        }

        var resolution = await homeResolver.ResolveAsync(sourceType, source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                resolution.Error ?? "無法解析 Antigravity CLI 資料目錄。");
        }

        try
        {
            if (!Directory.Exists(resolution.Path))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "找不到 Antigravity CLI 資料目錄。",
                    resolution.Path);
            }

            var projectsDirectory = Path.Combine(resolution.Path, "brain");
            if (!Directory.Exists(projectsDirectory))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "Antigravity CLI 資料目錄中找不到 brain。",
                    resolution.Path);
            }

            var firstFile = EnumerateSessionFiles(resolution.Path).FirstOrDefault();
            if (firstFile is not null)
            {
                var warnings = new List<string>();
                _ = ParseSession(firstFile, warnings, cancellationToken);
                if (warnings.Count > 0)
                {
                    return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable,
                        "Antigravity CLI 對話尚無法完整讀取，請確認格式或稍後重試。", warnings.ToArray());
                }
            }
            return SourceValidationResult.Valid(
                firstFile is null ? "Antigravity CLI 資料目錄可讀取，尚無對話。" : "Antigravity CLI 會話資料可讀取。",
                resolution.Path,
                sourceType == ActivitySourceType.WslAntigravityCli
                    ? $"WSL 環境：{SourceSettingsSerializer.DeserializeAntigravityCli(source.SettingsJson).Distro}"
                    : sourceType == ActivitySourceType.MacOsAntigravityCli ? "macOS" : "Windows");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Antigravity CLI 會話資料無法讀取。",
                exception.Message);
        }
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        var resolution = await homeResolver.ResolveAsync(sourceType, request.Source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            batch.Warnings.Add(resolution.Error ?? "無法解析 Antigravity CLI 資料目錄。");
            return batch;
        }

        var scanStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var allFiles = EnumerateSessionFiles(resolution.Path).ToList();
            var usageMetadata = ReadUsageMetadata(resolution.Path, batch.Warnings, cancellationToken);
            var checkpoint = DeserializeCheckpoint(request.Source.CheckpointJson);
            var isBackfill = !request.UpdateCheckpoint;
            var isInitialImport = request.UpdateCheckpoint && request.Source.LastSuccessAt is null;
            var watermark = (checkpoint.LastScanAt ?? request.Since).Subtract(Overlap);
            var retryPaths = checkpoint.RetryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = isBackfill || isInitialImport
                ? allFiles
                : allFiles.Where(file =>
                        file.LastWriteTimeUtc >= watermark.UtcDateTime ||
                        retryPaths.Contains(file.FullName))
                    .ToList();
            var nextRetryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextUnchangedFailures = new Dictionary<string, FailedFileState>(StringComparer.OrdinalIgnoreCase);
            var sessions = new Dictionary<string, ParsedSession>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in CollectionFileProgress.Track(candidates, request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var state = new FailedFileState(file.Length, file.LastWriteTimeUtc);
                    if (!isBackfill && !isInitialImport &&
                        checkpoint.UnchangedFailures.TryGetValue(file.FullName, out var previousState) &&
                        state == previousState)
                    {
                        nextRetryPaths.Add(file.FullName);
                        nextUnchangedFailures[file.FullName] = state;
                        continue;
                    }
                    var warnings = new List<string>();
                    var parsed = ParseSession(file, warnings, cancellationToken);
                    batch.Warnings.AddRange(warnings);
                    if (warnings.Count > 0)
                    {
                        nextRetryPaths.Add(file.FullName);
                        // Only remember stable reads; concurrent writes must be retried.
                        if (file.Length == state.Length && file.LastWriteTimeUtc == state.LastWriteTimeUtc)
                        {
                            nextUnchangedFailures[file.FullName] = state;
                        }
                    }

                    if (parsed is null)
                    {
                        continue;
                    }

                    if (usageMetadata.TryGetValue(parsed.SessionId, out var usage))
                    {
                        parsed = parsed with { Cwd = usage.Cwd, CliVersion = usage.Version };
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

                    if (!sessions.TryGetValue(parsed.SessionId, out var current) ||
                        parsed.UpdatedAt > current.UpdatedAt)
                    {
                        sessions[parsed.SessionId] = parsed;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    batch.Warnings.Add($"{file.Name}：{exception.Message}");
                    nextRetryPaths.Add(file.FullName);
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

                var metadata = new AntigravityCliSessionMetadata
                {
                    SessionId = session.SessionId,
                    UpdatedAt = session.UpdatedAt,
                    Cwd = session.Cwd,
                    Platform = sourceType.ToString(),
                    CliVersion = session.CliVersion,
                    IsComplete = session.IsComplete,
                    Messages = session.Messages
                };
                batch.Evidence.Add(new SourceEvidence
                {
                    SourceId = request.Source.Id,
                    ProjectId = request.Source.ProjectId,
                    RepositoryKey = RepositoryKey,
                    RepositoryPath = session.Cwd ?? string.Empty,
                    Environment = sourceType.ToString(),
                    Kind = EvidenceKind.AntigravityCliSession,
                    ExternalKey = $"session:{request.Source.Id:N}:{session.SessionId}",
                    Title = session.Title,
                    CommitMessage = userContext,
                    OccurredAt = session.OccurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeAntigravityCliMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                });
            }

            batch.SuccessfulRepositories = 1;
            if (request.UpdateCheckpoint)
            {
                batch.CheckpointJson = JsonSerializer.Serialize(new AntigravityCliCheckpoint
                {
                    LastScanAt = scanStartedAt,
                    RetryPaths = nextRetryPaths.ToList(),
                    UnchangedFailures = nextUnchangedFailures
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            batch.Warnings.Add(exception.Message);
        }

        return batch;
    }

    // Format reference: TokenUsageInsights src/db.rs and src/timeline.rs.
    // Read only the CLI's visible transcript; never parse binary conversations or credentials.
    private static IEnumerable<FileInfo> EnumerateSessionFiles(string root)
    {
        var brain = Path.Combine(root, "brain");
        foreach (var directory in Directory.EnumerateDirectories(brain))
        {
            var path = Path.Combine(directory, ".system_generated", "logs", "transcript_full.jsonl");
            if (File.Exists(path))
            {
                yield return new FileInfo(path);
            }
        }
    }

    private static ParsedSession? ParseSession(
        FileInfo file, List<string> warnings, CancellationToken cancellationToken)
    {
        var sessionId = file.Directory!.Parent!.Parent!.Name;
        var messages = new List<AntigravityCliSessionMessage>();
        var ordinal = 0;
        var complete = true;
        var recognized = false;
        var initialLength = file.Length;
        var initialModified = file.LastWriteTimeUtc;
        foreach (var line in ReadSharedLines(file.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                var type = GetString(row, "type");
                if (type is not ("USER_INPUT" or "PLANNER_RESPONSE")) continue;
                recognized = true;
                var text = GetString(row, "content");
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!TryGetTimestamp(row, "created_at", out var timestamp) &&
                    !TryGetTimestamp(row, "timestamp", out timestamp))
                {
                    complete = false;
                    warnings.Add($"會話 {sessionId}：第 {ordinal} 行缺少有效時間，檔案變動後會重試。");
                    continue;
                }
                messages.Add(new AntigravityCliSessionMessage
                {
                    Id = $"line:{ordinal}",
                    Role = type == "USER_INPUT" ? "user" : "assistant",
                    Timestamp = timestamp,
                    Text = text
                });
            }
            catch (JsonException)
            {
                complete = false;
                warnings.Add($"會話 {sessionId}：第 {ordinal} 行 JSON 格式無效或不完整，檔案變動後會重試。");
            }
        }
        file.Refresh();
        if (file.Length != initialLength || file.LastWriteTimeUtc != initialModified)
        {
            complete = false;
            warnings.Add($"會話 {sessionId}：讀取期間仍在寫入，已保留重試。");
        }
        if (!recognized && ordinal > 0)
        {
            warnings.Add($"會話 {sessionId}：尚未辨識到支援的對話格式。");
        }
        var firstUser = messages.FirstOrDefault(message => message.Role == "user");
        if (firstUser is null) return null;
        return new ParsedSession(sessionId, BuildTitle(firstUser.Text),
            messages.Min(message => message.Timestamp),
            new[] { messages.Max(message => message.Timestamp), new DateTimeOffset(initialModified, TimeSpan.Zero) }.Max(),
            null, null, complete, messages);
    }

    private static string BuildTitle(string text)
    {
        const string startTag = "<USER_REQUEST>";
        const string endTag = "</USER_REQUEST>";
        var start = text.IndexOf(startTag, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(endTag, start + startTag.Length, StringComparison.Ordinal);
        if (end >= 0) text = text[(start + startTag.Length)..end];
        var title = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) return "Antigravity CLI 對話";
        return title.Length > 120 ? title[..120] + "…" : title;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string? GetString(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool TryGetTimestamp(JsonElement row, string name, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(GetString(row, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);

    private static Dictionary<string, (string? Cwd, string? Version)> ReadUsageMetadata(
        string root, List<string> warnings, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string? Cwd, string? Version)>(StringComparer.Ordinal);
        var usage = Path.Combine(root, "usage");
        if (!Directory.Exists(usage)) return result;
        try
        {
            foreach (var path in Directory.EnumerateFiles(usage, "usage-*.jsonl").Order(StringComparer.Ordinal))
            {
                try
                {
                    foreach (var line in ReadSharedLines(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var row = document.RootElement;
                            var id = GetString(row, "session_id");
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            result.TryGetValue(id, out var previous);
                            result[id] = (GetString(row, "cwd") ?? previous.Cwd, GetString(row, "version") ?? previous.Version);
                        }
                        catch (JsonException) { /* Optional usage snapshots may still be writing. */ }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add("部分選填用量資料無法讀取；對話收集仍會繼續。");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add("無法列出選填用量資料；對話收集仍會繼續。");
        }
        return result;
    }

    private static AntigravityCliCheckpoint DeserializeCheckpoint(string? json)
    {
        try
        {
            var checkpoint = string.IsNullOrWhiteSpace(json)
                ? new AntigravityCliCheckpoint()
                : JsonSerializer.Deserialize<AntigravityCliCheckpoint>(json) ?? new AntigravityCliCheckpoint();
            checkpoint.RetryPaths ??= [];
            checkpoint.UnchangedFailures = new Dictionary<string, FailedFileState>(
                checkpoint.UnchangedFailures ?? [], StringComparer.OrdinalIgnoreCase);
            return checkpoint;
        }
        catch (JsonException)
        {
            return new AntigravityCliCheckpoint();
        }
    }

    private sealed class AntigravityCliCheckpoint
    {
        public DateTimeOffset? LastScanAt { get; set; }
        public List<string> RetryPaths { get; set; } = [];
        public Dictionary<string, FailedFileState> UnchangedFailures { get; set; } = [];
    }

    private sealed record FailedFileState(long Length, DateTime LastWriteTimeUtc);

    private sealed record ParsedSession(
        string SessionId,
        string Title,
        DateTimeOffset OccurredAt,
        DateTimeOffset UpdatedAt,
        string? Cwd,
        string? CliVersion,
        bool IsComplete,
        List<AntigravityCliSessionMessage> Messages);
}

public interface IAntigravityCliHomeResolver
{
    Task<AntigravityCliHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken);
}

public sealed record AntigravityCliHomeResolution(bool Succeeded, string? Path, string? Error)
{
    public static AntigravityCliHomeResolution Success(string path) => new(true, path, null);
    public static AntigravityCliHomeResolution Failure(string error) => new(false, null, error);
}

public sealed class AntigravityCliHomeResolver(IProcessRunner processRunner) : IAntigravityCliHomeResolver
{
    public async Task<AntigravityCliHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeAntigravityCli(source.SettingsJson);
        if (sourceType is ActivitySourceType.WindowsAntigravityCli or ActivitySourceType.MacOsAntigravityCli)
        {
            var value = string.IsNullOrWhiteSpace(settings.AntigravityCliHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli")
                : ExpandLocalHome(settings.AntigravityCliHome);
            try
            {
                return AntigravityCliHomeResolution.Success(Path.GetFullPath(value));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return AntigravityCliHomeResolution.Failure(exception.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Distro))
        {
            return AntigravityCliHomeResolution.Failure("WSL Antigravity CLI 來源必須指定 Linux 環境名稱，例如 Ubuntu。");
        }

        IReadOnlyList<string> arguments = string.IsNullOrWhiteSpace(settings.AntigravityCliHome)
            ? ["-d", settings.Distro, "--", "sh", "-lc", "wslpath -w -- \"$HOME/.gemini/antigravity-cli\""]
            : ["-d", settings.Distro, "--", "wslpath", "-w", "--", settings.AntigravityCliHome];
        var result = await processRunner.RunAsync(
            new ProcessRequest("wsl.exe", arguments),
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return AntigravityCliHomeResolution.Failure(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "無法從指定的 WSL 環境解析 Antigravity CLI 資料目錄。"
                    : result.StandardError.Trim());
        }

        return AntigravityCliHomeResolution.Success(result.StandardOutput.Trim());
    }

    private static string ExpandLocalHome(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return expanded;
    }
}
