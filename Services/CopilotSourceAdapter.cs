using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

/// <summary>
/// Reads the local session event stream written by GitHub Copilot CLI and the
/// Copilot application. The application uses the same session-state layout as
/// the CLI, so both products intentionally share this adapter.
/// </summary>
public sealed class CopilotSourceAdapter : IActivitySourceAdapter
{
    private const string RepositoryKey = "copilot-session";
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private readonly ICopilotHomeResolver homeResolver;
    private readonly ActivitySourceType sourceType;

    public CopilotSourceAdapter(ProcessRunner processRunner, ActivitySourceType sourceType)
        : this(new CopilotHomeResolver(processRunner), sourceType)
    {
    }

    public CopilotSourceAdapter(ICopilotHomeResolver homeResolver, ActivitySourceType sourceType)
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
        if (sourceType == ActivitySourceType.MacOsCopilot && !OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "macOS GitHub Copilot 來源只能在 macOS 上執行。");
        }

        if (sourceType == ActivitySourceType.WindowsCopilot && OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "Windows GitHub Copilot 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslCopilot && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "WSL GitHub Copilot 來源只能在 Windows 上執行。");
        }

        var resolution = await homeResolver.ResolveAsync(sourceType, source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                resolution.Error ?? "無法解析 GitHub Copilot 資料目錄。");
        }

        try
        {
            var rootStatus = SourcePathProbe.CheckDirectory(resolution.Path);
            if (rootStatus == SourcePathStatus.AccessDenied)
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "GitHub Copilot 資料目錄存取遭拒。",
                    resolution.Path);
            }

            if (rootStatus != SourcePathStatus.Readable)
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    rootStatus == SourcePathStatus.Unreadable
                        ? "GitHub Copilot 資料目錄無法讀取。"
                        : "找不到 GitHub Copilot 資料目錄。",
                    resolution.Path);
            }

            var sessionDirectory = Path.Combine(resolution.Path, "session-state");
            var sessionStatus = SourcePathProbe.CheckDirectory(sessionDirectory);
            if (sessionStatus != SourcePathStatus.Readable)
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    sessionStatus == SourcePathStatus.AccessDenied
                        ? "GitHub Copilot session-state 存取遭拒。"
                        : sessionStatus == SourcePathStatus.Unreadable
                            ? "GitHub Copilot session-state 無法讀取。"
                            : "GitHub Copilot 資料目錄中找不到 session-state。",
                    resolution.Path);
            }

            var scanDiagnostics = new List<string>();
            var hasSessionFiles = EnumerateSessionFiles(resolution.Path, scanDiagnostics).Any();
            var details = new List<string>
            {
                resolution.Path,
                hasSessionFiles ? "已找到 GitHub Copilot session 對話檔案。" : "目前沒有 GitHub Copilot session 對話檔案。",
                sourceType == ActivitySourceType.WslCopilot
                    ? $"WSL 環境：{SourceSettingsSerializer.DeserializeCopilot(source.SettingsJson).Distro}"
                    : sourceType == ActivitySourceType.MacOsCopilot ? "macOS" : "Windows"
            };
            details.AddRange(scanDiagnostics);
            return SourceValidationResult.Valid(
                scanDiagnostics.Count > 0
                    ? "GitHub Copilot 對話資料可讀取，但部分資料夾無法讀取。"
                    : !hasSessionFiles
                        ? "GitHub Copilot 資料目錄可讀取，但目前沒有對話檔案。"
                        : "GitHub Copilot 對話資料可讀取。",
                details.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "GitHub Copilot 對話資料無法讀取。",
                exception.Message);
        }
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        var resolution = await homeResolver.ResolveAsync(sourceType, request.Source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            batch.Warnings.Add(resolution.Error ?? "無法解析 GitHub Copilot 資料目錄。");
            return batch;
        }

        var scanStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var rootStatus = SourcePathProbe.CheckDirectory(resolution.Path);
            var sessionStatePath = Path.Combine(resolution.Path, "session-state");
            var sessionStateStatus = rootStatus == SourcePathStatus.Readable
                ? SourcePathProbe.CheckDirectory(sessionStatePath)
                : rootStatus;
            if (rootStatus != SourcePathStatus.Readable || sessionStateStatus != SourcePathStatus.Readable)
            {
                batch.Warnings.Add(rootStatus != SourcePathStatus.Readable
                    ? SourcePathProbe.Describe(resolution.Path, rootStatus)
                    : SourcePathProbe.Describe(sessionStatePath, sessionStateStatus));
                return batch;
            }

            var scanDiagnostics = new List<string>();
            var allFiles = EnumerateSessionFiles(resolution.Path, scanDiagnostics).ToList();
            batch.Warnings.AddRange(scanDiagnostics);
            var checkpoint = DeserializeCheckpoint(request.Source.CheckpointJson);
            var isBackfill = !request.UpdateCheckpoint;
            var isInitialImport = request.UpdateCheckpoint && request.Source.LastSuccessAt is null;
            var watermark = (checkpoint.LastScanAt ?? request.Since).Subtract(Overlap);
            var retryPaths = checkpoint.RetryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = isBackfill
                ? allFiles
                : allFiles.Where(file =>
                        file.LastWriteTimeUtc >= watermark.UtcDateTime ||
                        retryPaths.Contains(file.FullName))
                    .ToList();
            var nextRetryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sessions = new Dictionary<string, ParsedSession>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var parsed = ParseSession(file, batch.Warnings);
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

                    if (!sessions.TryGetValue(parsed.SessionId, out var current) ||
                        parsed.UpdatedAt > current.UpdatedAt ||
                        (parsed.UpdatedAt == current.UpdatedAt && parsed.IsComplete && !current.IsComplete))
                    {
                        sessions[parsed.SessionId] = parsed;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    batch.Warnings.Add($"{file.Name}：{exception.Message}");
                    nextRetryPaths.Add(file.FullName);
                }
            }

            var rootFingerprint = CopilotSourceIdentity.Fingerprint(resolution.Path);
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
                    Cwd = session.Cwd,
                    Platform = sourceType.ToString(),
                    SourceFormat = session.SourceFormat,
                    Client = session.Client,
                    SourcePath = session.FilePath,
                    IsComplete = session.IsComplete,
                    Messages = session.Messages
                };
                var sourceFormat = session.SourceFormat;
                batch.Evidence.Add(new SourceEvidence
                {
                    SourceId = request.Source.Id,
                    ProjectId = request.Source.ProjectId,
                    RepositoryKey = RepositoryKey,
                    RepositoryPath = session.Cwd ?? string.Empty,
                    Environment = sourceType.ToString(),
                    Kind = EvidenceKind.CopilotSession,
                    ExternalKey = $"session:{sourceFormat}:{rootFingerprint}:{session.SessionId}",
                    Title = session.Title,
                    CommitMessage = userContext,
                    OccurredAt = session.OccurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeCopilotMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                });
            }

            batch.SuccessfulRepositories = 1;
            if (request.UpdateCheckpoint)
            {
                batch.CheckpointJson = JsonSerializer.Serialize(new CopilotCheckpoint
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

        return batch;
    }

    private static IReadOnlyList<FileInfo> EnumerateSessionFiles(string root, ICollection<string>? diagnostics = null)
    {
        var directory = Path.Combine(root, "session-state");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<FileInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "events.jsonl", SearchOption.AllDirectories))
            {
                files.Add(new FileInfo(path));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics?.Add($"{directory}：無法列出 session-state：{exception.Message}");
        }

        return files;
    }

    private static ParsedSession? ParseSession(FileInfo file, List<string> warnings)
    {
        string? sessionId = null;
        string? cwd = null;
        string? client = null;
        DateTimeOffset? createdAt = null;
        DateTimeOffset? firstMessageAt = null;
        DateTimeOffset? lastRecordAt = null;
        var messages = new List<CopilotSessionMessage>();
        var complete = true;
        var ordinal = 0;
        file.Refresh();
        var initialLength = file.Length;
        var initialWriteTime = file.LastWriteTimeUtc;

        foreach (var line in ReadSharedLines(file.FullName))
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
                complete = false;
                continue;
            }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object)
                {
                    complete = false;
                    continue;
                }
                var payload = GetObject(row, "payload") ?? GetObject(row, "data") ?? row;
                var hasTimestamp = TryGetDateTimeOffset(row, "timestamp", out var timestamp) ||
                                   TryGetDateTimeOffset(payload, "timestamp", out timestamp);
                var rowTimestamp = hasTimestamp
                    ? timestamp
                    : createdAt ?? new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
                lastRecordAt = lastRecordAt is null || rowTimestamp > lastRecordAt ? rowTimestamp : lastRecordAt;

                var type = GetString(row, "type") ?? GetString(payload, "type") ?? string.Empty;
                if (IsSessionStartEvent(type))
                {
                    sessionId ??= FirstNonEmpty(
                        GetString(payload, "sessionId"),
                        GetString(payload, "session_id"),
                        GetString(payload, "id"),
                        GetString(row, "sessionId"),
                        GetString(row, "session_id"));
                    createdAt ??= FirstTimestamp(payload, "createdAt", "created_at", "timestamp");
                    cwd ??= FirstNonEmpty(
                        GetString(payload, "cwd"),
                        GetString(GetObject(payload, "context"), "cwd"));
                    client ??= FirstNonEmpty(
                        GetString(payload, "client"),
                        GetString(payload, "clientName"),
                        GetString(payload, "app"));
                    continue;
                }

                sessionId ??= FirstNonEmpty(
                    GetString(payload, "sessionId"),
                    GetString(payload, "session_id"),
                    GetString(row, "sessionId"),
                    GetString(row, "session_id"));
                cwd ??= FirstNonEmpty(
                    GetString(payload, "cwd"),
                    GetString(GetObject(payload, "context"), "cwd"));

                if (IsUserMessageEvent(type))
                {
                    if (AddMessage(messages, "user", payload, rowTimestamp, ordinal) && hasTimestamp &&
                        (firstMessageAt is null || rowTimestamp < firstMessageAt.Value))
                    {
                        firstMessageAt = rowTimestamp;
                    }
                }
                else if (IsAssistantMessageEvent(type))
                {
                    if (AddMessage(messages, "assistant", payload, rowTimestamp, ordinal) && hasTimestamp &&
                        (firstMessageAt is null || rowTimestamp < firstMessageAt.Value))
                    {
                        firstMessageAt = rowTimestamp;
                    }
                }
            }
        }

        if (HasFileChanged(file, initialLength, initialWriteTime))
        {
            complete = false;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = file.Directory?.Name;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            warnings.Add($"{file.Name}：找不到有效的 Copilot session id，已略過。");
            return null;
        }

        var normalizedId = sessionId.Trim();
        var visibleMessages = messages
            .GroupBy(message => $"{message.Role}:{message.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(message => message.Text.Length)
                .ThenByDescending(message => message.Timestamp)
                .First())
            .OrderBy(message => message.Timestamp)
            .ToList();
        var firstUser = visibleMessages.FirstOrDefault(message => message.Role == "user");

        if (!complete)
        {
            warnings.Add($"{file.Name}：包含尚未完成或格式錯誤的 JSONL，已保留可讀部分並安排重試。");
        }

        var occurredAt = firstMessageAt ?? createdAt;
        if (occurredAt is null)
        {
            warnings.Add($"{file.Name}：找不到 session 建立時間或有效訊息時間，已略過。");
            return null;
        }

        var updatedAt = new[]
            {
                lastRecordAt ?? occurredAt.Value,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)
            }
            .Max();
        return new ParsedSession(
            normalizedId,
            BuildTitle(firstUser?.Text ?? normalizedId),
            occurredAt.Value,
            updatedAt,
            cwd,
            string.IsNullOrWhiteSpace(client) ? "GitHub Copilot" : client.Trim(),
            file.FullName,
            complete,
            visibleMessages,
            "copilot-session-state-jsonl");
    }

    private static bool AddMessage(
        List<CopilotSessionMessage> messages,
        string role,
        JsonElement payload,
        DateTimeOffset timestamp,
        int ordinal)
    {
        var text = FirstVisibleText(
            GetValue(payload, "content"),
            GetValue(payload, "text"),
            GetValue(payload, "message"),
            GetValue(payload, "output"));
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var id = FirstNonEmpty(
            GetString(payload, "id"),
            GetString(payload, "messageId"),
            GetString(payload, "message_id"),
            GetString(payload, "uuid")) ?? $"{role}:{ordinal}";
        messages.Add(new CopilotSessionMessage
        {
            Id = id,
            Role = role,
            Timestamp = timestamp,
            Text = text
        });
        return true;
    }

    private static string FirstVisibleText(params JsonElement?[] values)
    {
        foreach (var value in values)
        {
            if (value is JsonElement element)
            {
                var text = ReadVisibleText(element);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadVisibleText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsHiddenPart(element))
            {
                return string.Empty;
            }

            foreach (var name in new[] { "text", "content", "value", "markdown" })
            {
                if (element.TryGetProperty(name, out var nested))
                {
                    var text = ReadVisibleText(nested);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            element.EnumerateArray()
                .Select(ReadVisibleText)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
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

    private static JsonElement? GetValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : null;

    private static bool IsHiddenPart(JsonElement element)
    {
        foreach (var name in new[] { "kind", "type", "category" })
        {
            if (GetString(element, name) is { } value &&
                (value.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("invocation", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("attachment", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSessionStartEvent(string type) =>
        type.Equals("session.start", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("session_meta", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("session.started", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("session_started", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserMessageEvent(string type) =>
        type.Equals("user.message", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("user_prompt", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("user_message", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssistantMessageEvent(string type) =>
        type.Equals("assistant.message", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("assistant_reply", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("assistant_message", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("assistant.response", StringComparison.OrdinalIgnoreCase);

    private static JsonElement? GetObject(JsonElement? element, string name)
    {
        if (element is not JsonElement value || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return property;
    }

    private static string? GetString(JsonElement? element, string name) =>
        element is JsonElement value && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static DateTimeOffset? FirstTimestamp(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetDateTimeOffset(element, name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetDateTimeOffset(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var milliseconds))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return property.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   property.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out value);
    }

    private static bool HasFileChanged(FileInfo file, long initialLength, DateTime initialWriteTime)
    {
        file.Refresh();
        return file.Length != initialLength || file.LastWriteTimeUtc != initialWriteTime;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static CopilotCheckpoint DeserializeCheckpoint(string? json)
    {
        try
        {
            var checkpoint = string.IsNullOrWhiteSpace(json)
                ? new CopilotCheckpoint()
                : JsonSerializer.Deserialize<CopilotCheckpoint>(json) ?? new CopilotCheckpoint();
            checkpoint.RetryPaths ??= [];
            return checkpoint;
        }
        catch (JsonException)
        {
            return new CopilotCheckpoint();
        }
    }

    private sealed class CopilotCheckpoint
    {
        public DateTimeOffset? LastScanAt { get; set; }
        public List<string> RetryPaths { get; set; } = [];
    }

    private sealed record ParsedSession(
        string SessionId,
        string Title,
        DateTimeOffset OccurredAt,
        DateTimeOffset UpdatedAt,
        string? Cwd,
        string Client,
        string FilePath,
        bool IsComplete,
        List<CopilotSessionMessage> Messages,
        string SourceFormat);
}

public interface ICopilotHomeResolver
{
    Task<CopilotHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken);
}

public sealed record CopilotHomeResolution(bool Succeeded, string? Path, string? Error)
{
    public static CopilotHomeResolution Success(string path) => new(true, path, null);
    public static CopilotHomeResolution Failure(string error) => new(false, null, error);
}

public sealed class CopilotHomeResolver(IProcessRunner processRunner) : ICopilotHomeResolver
{
    public async Task<CopilotHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeCopilot(source.SettingsJson);
        if (sourceType is ActivitySourceType.WindowsCopilot or ActivitySourceType.MacOsCopilot)
        {
            var configured = settings.CopilotHome;
            var value = string.IsNullOrWhiteSpace(configured)
                ? Environment.GetEnvironmentVariable("COPILOT_HOME")
                : configured;
            value = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot")
                : ExpandLocalHome(value);
            try
            {
                return CopilotHomeResolution.Success(Path.GetFullPath(value));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return CopilotHomeResolution.Failure(exception.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Distro))
        {
            return CopilotHomeResolution.Failure("WSL GitHub Copilot 來源必須指定 Linux 環境名稱，例如 Ubuntu。");
        }

        IReadOnlyList<string> arguments = string.IsNullOrWhiteSpace(settings.CopilotHome)
            ? ["-d", settings.Distro, "--", "sh", "-lc", "wslpath -w -- \"${COPILOT_HOME:-$HOME/.copilot}\""]
            : ["-d", settings.Distro, "--", "wslpath", "-w", "--", settings.CopilotHome];
        var result = await processRunner.RunAsync(
            new ProcessRequest("wsl.exe", arguments),
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return CopilotHomeResolution.Failure(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "無法從指定的 WSL 環境解析 GitHub Copilot 資料目錄。"
                    : result.StandardError.Trim());
        }

        return CopilotHomeResolution.Success(result.StandardOutput.Trim());
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

public static class CopilotSourceIdentity
{
    public static string Fingerprint(string path)
    {
        var normalized = NormalizePath(path);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var normalized = !string.IsNullOrEmpty(root) &&
                         string.Equals(fullPath, root, StringComparison.Ordinal)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}
