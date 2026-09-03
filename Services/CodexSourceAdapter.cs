using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed partial class CodexSourceAdapter : IActivitySourceAdapter
{
    private const string RepositoryKey = "codex-session";
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private readonly ICodexHomeResolver homeResolver;
    private readonly ActivitySourceType sourceType;

    public CodexSourceAdapter(ProcessRunner processRunner, ActivitySourceType sourceType)
        : this(new CodexHomeResolver(processRunner), sourceType)
    {
    }

    public CodexSourceAdapter(ICodexHomeResolver homeResolver, ActivitySourceType sourceType)
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
        if (sourceType == ActivitySourceType.MacOsCodex && OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "macOS Codex 來源只能在 macOS 上執行。");
        }

        if (sourceType == ActivitySourceType.WindowsCodex && OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "Windows Codex 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslCodex && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "WSL Codex 來源只能在 Windows 上執行。");
        }

        var resolution = await ResolveHomeAsync(source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                resolution.Error ?? "無法解析 Codex 資料目錄。");
        }

        try
        {
            if (!Directory.Exists(resolution.Path))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "找不到 Codex 資料目錄。",
                    resolution.Path);
            }

            var sessionDirectory = Path.Combine(resolution.Path, "sessions");
            var archiveDirectory = Path.Combine(resolution.Path, "archived_sessions");
            if (!Directory.Exists(sessionDirectory) && !Directory.Exists(archiveDirectory))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "Codex 資料目錄中找不到 sessions 或 archived_sessions。",
                    resolution.Path);
            }

            _ = EnumerateSessionFiles(resolution.Path).Take(1).ToList();
            return SourceValidationResult.Valid(
                "Codex 會話資料可讀取。",
                resolution.Path,
                sourceType == ActivitySourceType.WslCodex
                    ? $"WSL 環境：{SourceSettingsSerializer.DeserializeCodex(source.SettingsJson).Distro}"
                    : sourceType == ActivitySourceType.MacOsCodex ? "macOS" : "Windows");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Codex 會話資料無法讀取。",
                exception.Message);
        }
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        var resolution = await ResolveHomeAsync(request.Source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            batch.Warnings.Add(resolution.Error ?? "無法解析 Codex 資料目錄。");
            return batch;
        }

        var scanStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var allFiles = EnumerateSessionFiles(resolution.Path).ToList();
            var index = LoadIndex(resolution.Path, batch.Warnings);
            var checkpoint = DeserializeCheckpoint(request.Source.CheckpointJson);
            var isBackfill = !request.UpdateCheckpoint;
            var isInitialImport = request.UpdateCheckpoint && request.Source.LastSuccessAt is null;
            var watermark = (checkpoint.LastScanAt ?? request.Since).Subtract(Overlap);
            var indexChangedIds = index.UpdatedAtById
                .Where(item => item.Value >= watermark)
                .Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = isBackfill
                ? allFiles
                : allFiles.Where(file =>
                        file.LastWriteTimeUtc >= watermark.UtcDateTime ||
                        (TryGetSessionIdFromFileName(file.Name, out var id) && indexChangedIds.Contains(id)))
                    .ToList();

            var sessions = new Dictionary<string, ParsedSession>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var parsed = ParseSession(file, index.TitlesById);
                    if (parsed is null)
                    {
                        batch.Warnings.Add($"{file.Name}：找不到有效的 session id，已略過。");
                        continue;
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
                        parsed.UpdatedAt > current.UpdatedAt ||
                        (parsed.UpdatedAt == current.UpdatedAt && !parsed.Archived && current.Archived))
                    {
                        sessions[parsed.SessionId] = parsed;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    batch.Warnings.Add($"{file.Name}：{exception.Message}");
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
                var metadata = new CodexSessionMetadata
                {
                    SessionId = session.SessionId,
                    UpdatedAt = session.UpdatedAt,
                    Cwd = session.Cwd,
                    Platform = sourceType.ToString(),
                    Archived = session.Archived,
                    CliVersion = session.CliVersion,
                    AttachmentCount = session.AttachmentCount,
                    Messages = session.Messages
                };
                batch.Evidence.Add(new SourceEvidence
                {
                    SourceId = request.Source.Id,
                    ProjectId = request.Source.ProjectId,
                    RepositoryKey = RepositoryKey,
                    RepositoryPath = session.Cwd ?? string.Empty,
                    Environment = sourceType.ToString(),
                    Kind = EvidenceKind.CodexSession,
                    ExternalKey = $"session:{session.SessionId}",
                    Title = session.Title,
                    CommitMessage = userContext,
                    OccurredAt = session.OccurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                });
            }

            batch.SuccessfulRepositories = 1;
            if (request.UpdateCheckpoint)
            {
                batch.CheckpointJson = JsonSerializer.Serialize(new CodexCheckpoint { LastScanAt = scanStartedAt });
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            batch.Warnings.Add(exception.Message);
        }

        return batch;
    }

    private Task<CodexHomeResolution> ResolveHomeAsync(
        ActivitySource source,
        CancellationToken cancellationToken) =>
        homeResolver.ResolveAsync(sourceType, source, cancellationToken);

    private static IEnumerable<FileInfo> EnumerateSessionFiles(string root)
    {
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
            {
                yield return new FileInfo(path);
            }
        }
    }

    private static SessionIndex LoadIndex(string root, List<string> warnings)
    {
        var result = new SessionIndex();
        var path = Path.Combine(root, "session_index.jsonl");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var rootElement = document.RootElement;
                    if (!TryNormalizeSessionId(GetString(rootElement, "id"), out var id))
                    {
                        continue;
                    }

                    var title = GetString(rootElement, "thread_name");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        result.TitlesById[id] = title.Trim();
                    }

                    if (TryGetDateTimeOffset(rootElement, "updated_at", out var updatedAt))
                    {
                        result.UpdatedAtById[id] = updatedAt;
                    }
                }
                catch (JsonException)
                {
                    // The index is append-only and its last line can be in progress.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"session_index.jsonl：{exception.Message}");
        }

        return result;
    }

    private static ParsedSession? ParseSession(
        FileInfo file,
        IReadOnlyDictionary<string, string> titles)
    {
        string? sessionId = null;
        string? cwd = null;
        string? cliVersion = null;
        DateTimeOffset? createdAt = null;
        DateTimeOffset? firstRecordAt = null;
        DateTimeOffset? lastRecordAt = null;
        var eventMessages = new List<CodexSessionMessage>();
        var responseMessages = new List<CodexSessionMessage>();
        var attachmentCount = 0;
        var ordinal = 0;

        foreach (var line in File.ReadLines(file.FullName))
        {
            ordinal++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var row = document.RootElement;
                var rowTimestamp = TryGetDateTimeOffset(row, "timestamp", out var timestamp)
                    ? timestamp
                    : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                firstRecordAt ??= rowTimestamp;
                lastRecordAt = lastRecordAt is null || rowTimestamp > lastRecordAt ? rowTimestamp : lastRecordAt;
                var type = GetString(row, "type");
                if (!row.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var payloadType = GetString(payload, "type");
                if (type == "session_meta")
                {
                    var rawId = GetString(payload, "session_id") ?? GetString(payload, "id");
                    if (TryNormalizeSessionId(rawId, out var normalized))
                    {
                        sessionId = normalized;
                    }
                    if (TryGetDateTimeOffset(payload, "timestamp", out var sessionTimestamp))
                    {
                        createdAt = sessionTimestamp;
                    }
                    cwd = GetString(payload, "cwd");
                    cliVersion = GetString(payload, "cli_version");
                    continue;
                }

                if (type == "event_msg" && payloadType == "user_message")
                {
                    AddMessage(eventMessages, "user", GetString(payload, "client_id"), GetString(payload, "message"), rowTimestamp, ordinal);
                    attachmentCount += CountArray(payload, "images") + CountArray(payload, "local_images") +
                                       CountArray(payload, "audio") + CountArray(payload, "local_audio");
                    continue;
                }

                if (type == "event_msg" && payloadType == "agent_message")
                {
                    AddMessage(eventMessages, "assistant", null, GetString(payload, "message"), rowTimestamp, ordinal);
                    continue;
                }

                if (type == "event_msg" && payloadType == "item_completed" &&
                    payload.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    var itemType = GetString(item, "type");
                    var role = itemType == "UserMessage" ? "user" : itemType == "AgentMessage" ? "assistant" : null;
                    if (role is not null)
                    {
                        var text = ReadContentText(item);
                        AddMessage(eventMessages, role, GetString(item, "id"), text, rowTimestamp, ordinal);
                        attachmentCount += CountNonTextContent(item);
                    }
                    continue;
                }

                if (type == "response_item" && payloadType == "message")
                {
                    var role = GetString(payload, "role");
                    if (role is "user" or "assistant")
                    {
                        var text = ReadContentText(payload);
                        if (!IsInjectedContext(text))
                        {
                            AddMessage(responseMessages, role, GetString(payload, "id"), text, rowTimestamp, ordinal);
                        }
                    }
                }
            }
        }

        if (sessionId is null && TryGetSessionIdFromFileName(file.Name, out var fileId))
        {
            sessionId = fileId;
        }
        if (sessionId is null)
        {
            return null;
        }

        var messages = DeduplicateMessages(eventMessages.Count > 0 ? eventMessages : responseMessages);
        var occurredAt = createdAt ?? firstRecordAt ?? TryGetTimestampFromFileName(file.Name) ??
            new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
        var updatedAt = new[]
            {
                lastRecordAt ?? DateTimeOffset.MinValue,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)
            }
            .Max();
        var title = titles.TryGetValue(sessionId, out var indexedTitle)
            ? indexedTitle
            : BuildFallbackTitle(messages, sessionId);
        var archived = file.FullName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "archived_sessions", StringComparison.OrdinalIgnoreCase));
        return new ParsedSession(
            sessionId,
            title,
            occurredAt,
            updatedAt,
            cwd,
            cliVersion,
            archived,
            attachmentCount,
            messages);
    }

    private static List<CodexSessionMessage> DeduplicateMessages(IEnumerable<CodexSessionMessage> messages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CodexSessionMessage>();
        foreach (var message in messages.OrderBy(item => item.Timestamp))
        {
            if (seen.Add(message.Id))
            {
                result.Add(message);
            }
        }
        return result;
    }

    private static void AddMessage(
        List<CodexSessionMessage> destination,
        string role,
        string? id,
        string? text,
        DateTimeOffset timestamp,
        int ordinal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        destination.Add(new CodexSessionMessage
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"{role}:{ordinal}" : id,
            Role = role,
            Timestamp = timestamp,
            Text = text
        });
    }

    private static string ReadContentText(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        return string.Join(
            Environment.NewLine,
            content.EnumerateArray()
                .Select(item => GetString(item, "text"))
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static int CountNonTextContent(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        return content.EnumerateArray().Count(item =>
        {
            var type = GetString(item, "type");
            return type is not null && !type.Equals("text", StringComparison.OrdinalIgnoreCase) &&
                   !type.EndsWith("_text", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int CountArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static bool IsInjectedContext(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<recommended_plugins>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<app-context>", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFallbackTitle(IReadOnlyList<CodexSessionMessage> messages, string sessionId)
    {
        var firstUserMessage = messages.FirstOrDefault(message => message.Role == "user")?.Text;
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return $"Codex session {sessionId[..Math.Min(8, sessionId.Length)]}";
        }
        var singleLine = WhitespaceRegex().Replace(firstUserMessage.Trim(), " ");
        return singleLine.Length <= 120 ? singleLine : singleLine[..120] + "…";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetDateTimeOffset(JsonElement element, string name, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            GetString(element, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out value);

    private static bool TryNormalizeSessionId(string? raw, out string normalized)
    {
        if (Guid.TryParse(raw, out var id))
        {
            normalized = id.ToString("D").ToLowerInvariant();
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    private static bool TryGetSessionIdFromFileName(string name, out string id)
    {
        var match = SessionIdRegex().Match(name);
        return TryNormalizeSessionId(match.Success ? match.Value : null, out id);
    }

    private static DateTimeOffset? TryGetTimestampFromFileName(string name)
    {
        var match = FileTimestampRegex().Match(name);
        if (!match.Success)
        {
            return null;
        }
        return DateTimeOffset.TryParseExact(
            match.Groups[1].Value,
            "yyyy-MM-dd'T'HH-mm-ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var value)
            ? value
            : null;
    }

    private static CodexCheckpoint DeserializeCheckpoint(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new CodexCheckpoint()
                : JsonSerializer.Deserialize<CodexCheckpoint>(json) ?? new CodexCheckpoint();
        }
        catch (JsonException)
        {
            return new CodexCheckpoint();
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex SessionIdRegex();

    [GeneratedRegex("rollout-(\\d{4}-\\d{2}-\\d{2}T\\d{2}-\\d{2}-\\d{2})-")]
    private static partial Regex FileTimestampRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class CodexCheckpoint
    {
        public DateTimeOffset? LastScanAt { get; set; }
    }

    private sealed class SessionIndex
    {
        public Dictionary<string, string> TitlesById { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> UpdatedAtById { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ParsedSession(
        string SessionId,
        string Title,
        DateTimeOffset OccurredAt,
        DateTimeOffset UpdatedAt,
        string? Cwd,
        string? CliVersion,
        bool Archived,
        int AttachmentCount,
        List<CodexSessionMessage> Messages);

}

public interface ICodexHomeResolver
{
    Task<CodexHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken);
}

public sealed record CodexHomeResolution(bool Succeeded, string? Path, string? Error)
{
    public static CodexHomeResolution Success(string path) => new(true, path, null);
    public static CodexHomeResolution Failure(string error) => new(false, null, error);
}

public sealed class CodexHomeResolver(IProcessRunner processRunner) : ICodexHomeResolver
{
    public async Task<CodexHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeCodex(source.SettingsJson);
        if (sourceType is ActivitySourceType.WindowsCodex or ActivitySourceType.MacOsCodex)
        {
            var configured = settings.CodexHome;
            var value = string.IsNullOrWhiteSpace(configured)
                ? Environment.GetEnvironmentVariable("CODEX_HOME")
                : configured;
            value = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : ExpandLocalHome(value);
            try
            {
                return CodexHomeResolution.Success(Path.GetFullPath(value));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return CodexHomeResolution.Failure(exception.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Distro))
        {
            return CodexHomeResolution.Failure("WSL Codex 來源必須指定 Linux 環境名稱，例如 Ubuntu。");
        }

        IReadOnlyList<string> arguments = string.IsNullOrWhiteSpace(settings.CodexHome)
            ? ["-d", settings.Distro, "--", "sh", "-lc", "wslpath -w -- \"${CODEX_HOME:-$HOME/.codex}\""]
            : ["-d", settings.Distro, "--", "wslpath", "-w", "--", settings.CodexHome];
        var result = await processRunner.RunAsync(
            new ProcessRequest("wsl.exe", arguments),
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return CodexHomeResolution.Failure(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "無法從指定的 WSL 環境解析 Codex 資料目錄。"
                    : result.StandardError.Trim());
        }

        return CodexHomeResolution.Success(result.StandardOutput.Trim());
    }

    private static string ExpandLocalHome(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith($"~{Path.DirectorySeparatorChar}") || expanded.StartsWith("~/"))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return expanded;
    }
}
