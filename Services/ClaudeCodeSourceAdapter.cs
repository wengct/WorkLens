using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ClaudeCodeSourceAdapter : IActivitySourceAdapter
{
    private const string RepositoryKey = "claude-code-session";
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private readonly IClaudeCodeHomeResolver homeResolver;
    private readonly ActivitySourceType sourceType;

    public ClaudeCodeSourceAdapter(ProcessRunner processRunner, ActivitySourceType sourceType)
        : this(new ClaudeCodeHomeResolver(processRunner), sourceType)
    {
    }

    public ClaudeCodeSourceAdapter(IClaudeCodeHomeResolver homeResolver, ActivitySourceType sourceType)
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
        if (sourceType == ActivitySourceType.MacOsClaudeCode && !OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "macOS Claude Code 來源只能在 macOS 上執行。");
        }

        if (sourceType == ActivitySourceType.WindowsClaudeCode && OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "Windows Claude Code 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslClaudeCode && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "WSL Claude Code 來源只能在 Windows 上執行。");
        }

        var resolution = await homeResolver.ResolveAsync(sourceType, source, cancellationToken);
        if (!resolution.Succeeded || resolution.Path is null)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                resolution.Error ?? "無法解析 Claude Code 資料目錄。");
        }

        try
        {
            if (!Directory.Exists(resolution.Path))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "找不到 Claude Code 資料目錄。",
                    resolution.Path);
            }

            var projectsDirectory = Path.Combine(resolution.Path, "projects");
            if (!Directory.Exists(projectsDirectory))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "Claude Code 資料目錄中找不到 projects。",
                    resolution.Path);
            }

            _ = EnumerateSessionFiles(resolution.Path).Take(1).ToList();
            return SourceValidationResult.Valid(
                "Claude Code 會話資料可讀取。",
                resolution.Path,
                sourceType == ActivitySourceType.WslClaudeCode
                    ? $"WSL 環境：{SourceSettingsSerializer.DeserializeClaudeCode(source.SettingsJson).Distro}"
                    : sourceType == ActivitySourceType.MacOsClaudeCode ? "macOS" : "Windows");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "Claude Code 會話資料無法讀取。",
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
            batch.Warnings.Add(resolution.Error ?? "無法解析 Claude Code 資料目錄。");
            return batch;
        }

        var scanStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var allFiles = EnumerateSessionFiles(resolution.Path).ToList();
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
                    var warnings = new List<string>();
                    var parsed = ParseSession(file, warnings);
                    batch.Warnings.AddRange(warnings);
                    if (warnings.Count > 0)
                    {
                        nextRetryPaths.Add(file.FullName);
                    }

                    if (parsed is null)
                    {
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

                var metadata = new ClaudeCodeSessionMetadata
                {
                    SessionId = session.SessionId,
                    UpdatedAt = session.UpdatedAt,
                    Cwd = session.Cwd,
                    Platform = sourceType.ToString(),
                    CliVersion = session.CliVersion,
                    AttachmentCount = session.AttachmentCount,
                    Messages = session.Messages
                };
                batch.Evidence.Add(new SourceEvidence
                {
                    SourceId = request.Source.Id,
                    ProjectId = request.Source.ProjectId,
                    RepositoryKey = RepositoryKey,
                    RepositoryPath = session.Cwd ?? session.ProjectDirectory,
                    Environment = sourceType.ToString(),
                    Kind = EvidenceKind.ClaudeCodeSession,
                    ExternalKey = $"session:{session.SessionId}",
                    Title = session.Title,
                    CommitMessage = userContext,
                    OccurredAt = session.OccurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeClaudeCodeMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                });
            }

            batch.SuccessfulRepositories = 1;
            if (request.UpdateCheckpoint)
            {
                batch.CheckpointJson = JsonSerializer.Serialize(new ClaudeCodeCheckpoint
                {
                    LastScanAt = scanStartedAt,
                    RetryPaths = nextRetryPaths.ToList()
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            batch.Warnings.Add(exception.Message);
        }

        return batch;
    }

    private static IEnumerable<FileInfo> EnumerateSessionFiles(string root)
    {
        var projectsDirectory = Path.Combine(root, "projects");
        if (!Directory.Exists(projectsDirectory))
        {
            yield break;
        }

        foreach (var projectDirectory in Directory.EnumerateDirectories(projectsDirectory))
        {
            // Subdirectories contain sidechain/subagent transcripts. Only the project
            // directory's own JSONL files are primary Claude Code sessions.
            var files = Directory.EnumerateFiles(projectDirectory, "*.jsonl", SearchOption.TopDirectoryOnly);
            foreach (var path in files)
            {
                var fileName = Path.GetFileName(path);
                if (fileName.Contains(".orphaned-", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains(".superseded-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new FileInfo(path);
            }
        }
    }

    private static ParsedSession? ParseSession(FileInfo file, List<string> warnings)
    {
        string? sessionId = null;
        string? cwd = null;
        string? cliVersion = null;
        DateTimeOffset? lastRecordAt = null;
        var messages = new List<ClaudeCodeSessionMessage>();
        var attachmentCount = 0;
        var ordinal = 0;

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
                warnings.Add($"{file.Name}：第 {ordinal} 行不是有效 JSON，已略過並保留重試。");
                continue;
            }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var rowTimestamp = TryGetDateTimeOffset(row, "timestamp", out var timestamp)
                    ? timestamp
                    : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                lastRecordAt = lastRecordAt is null || rowTimestamp > lastRecordAt ? rowTimestamp : lastRecordAt;
                var type = GetString(row, "type");
                sessionId ??= NormalizeSessionId(GetString(row, "sessionId") ?? GetString(row, "session_id"));
                cwd ??= GetString(row, "cwd");
                cliVersion ??= GetString(row, "version") ?? GetString(row, "cli_version");
                if (type is not ("user" or "assistant") ||
                    GetBoolean(row, "isMeta") ||
                    GetBoolean(row, "isSidechain") ||
                    !row.TryGetProperty("message", out var message))
                {
                    continue;
                }

                var role = type == "user" ? "user" : "assistant";
                if (message.ValueKind == JsonValueKind.Object)
                {
                    sessionId ??= NormalizeSessionId(GetString(message, "sessionId") ?? GetString(message, "session_id"));
                }
                var text = ReadVisibleText(message, out var attachments);
                attachmentCount += attachments;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var messageId = GetString(row, "uuid");
                if (messageId is null && message.ValueKind == JsonValueKind.Object)
                {
                    messageId = GetString(message, "id");
                }
                messages.Add(new ClaudeCodeSessionMessage
                {
                    Id = messageId ?? $"{role}:{ordinal}",
                    Role = role,
                    Timestamp = rowTimestamp,
                    Text = text
                });
            }
        }

        sessionId ??= NormalizeSessionId(Path.GetFileNameWithoutExtension(file.Name));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var visibleMessages = DeduplicateMessages(messages);
        var firstUser = visibleMessages.FirstOrDefault(message => message.Role == "user");
        if (firstUser is null)
        {
            return null;
        }

        var occurredAt = visibleMessages.Min(message => message.Timestamp);
        var updatedAt = new[]
            {
                lastRecordAt ?? occurredAt,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)
            }
            .Max();
        var projectDirectory = file.Directory?.FullName ?? string.Empty;
        return new ParsedSession(
            sessionId,
            BuildTitle(firstUser.Text),
            occurredAt,
            updatedAt,
            cwd,
            cliVersion,
            attachmentCount,
            projectDirectory,
            visibleMessages);
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

    private static List<ClaudeCodeSessionMessage> DeduplicateMessages(
        IEnumerable<ClaudeCodeSessionMessage> messages) =>
        messages
            .GroupBy(message => message.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(message => message.Timestamp)
                .ThenByDescending(message => message.Text.Length)
                .First())
            .OrderBy(message => message.Timestamp)
            .ToList();

    private static string ReadVisibleText(JsonElement element, out int attachmentCount)
    {
        attachmentCount = 0;
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Object && element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var content = element.ValueKind == JsonValueKind.Object &&
                      element.TryGetProperty("content", out var objectContent)
            ? objectContent
            : element;
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            var contentType = GetString(content, "type");
            if (contentType is "text" or "input_text" or "output_text" or null)
            {
                return GetString(content, "text") ?? string.Empty;
            }

            if (contentType is "image" or "input_image" or "document")
            {
                attachmentCount = 1;
            }
            return string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var texts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                texts.Add(item.GetString() ?? string.Empty);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(item, "type");
            if (type is "text" or "input_text" or "output_text" or null)
            {
                var text = GetString(item, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }
            }
            else if (type is "image" or "input_image" or "document")
            {
                attachmentCount++;
            }
        }

        return string.Join(Environment.NewLine, texts.Where(text => !string.IsNullOrWhiteSpace(text)));
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
        return title.Length <= 120 ? title : title[..120] + "…";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static bool TryGetDateTimeOffset(JsonElement element, string name, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            GetString(element, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out value);

    private static string? NormalizeSessionId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ClaudeCodeCheckpoint DeserializeCheckpoint(string? json)
    {
        try
        {
            var checkpoint = string.IsNullOrWhiteSpace(json)
                ? new ClaudeCodeCheckpoint()
                : JsonSerializer.Deserialize<ClaudeCodeCheckpoint>(json) ?? new ClaudeCodeCheckpoint();
            checkpoint.RetryPaths ??= [];
            return checkpoint;
        }
        catch (JsonException)
        {
            return new ClaudeCodeCheckpoint();
        }
    }

    private sealed class ClaudeCodeCheckpoint
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
        string? CliVersion,
        int AttachmentCount,
        string ProjectDirectory,
        List<ClaudeCodeSessionMessage> Messages);
}

public interface IClaudeCodeHomeResolver
{
    Task<ClaudeCodeHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken);
}

public sealed record ClaudeCodeHomeResolution(bool Succeeded, string? Path, string? Error)
{
    public static ClaudeCodeHomeResolution Success(string path) => new(true, path, null);
    public static ClaudeCodeHomeResolution Failure(string error) => new(false, null, error);
}

public sealed class ClaudeCodeHomeResolver(IProcessRunner processRunner) : IClaudeCodeHomeResolver
{
    public async Task<ClaudeCodeHomeResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeClaudeCode(source.SettingsJson);
        if (sourceType is ActivitySourceType.WindowsClaudeCode or ActivitySourceType.MacOsClaudeCode)
        {
            var configured = settings.ClaudeCodeHome;
            var value = string.IsNullOrWhiteSpace(configured)
                ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                : configured;
            value = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : ExpandLocalHome(value);
            try
            {
                return ClaudeCodeHomeResolution.Success(Path.GetFullPath(value));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return ClaudeCodeHomeResolution.Failure(exception.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Distro))
        {
            return ClaudeCodeHomeResolution.Failure("WSL Claude Code 來源必須指定 Linux 環境名稱，例如 Ubuntu。");
        }

        IReadOnlyList<string> arguments = string.IsNullOrWhiteSpace(settings.ClaudeCodeHome)
            ? ["-d", settings.Distro, "--", "sh", "-lc", "wslpath -w -- \"${CLAUDE_CONFIG_DIR:-$HOME/.claude}\""]
            : ["-d", settings.Distro, "--", "wslpath", "-w", "--", settings.ClaudeCodeHome];
        var result = await processRunner.RunAsync(
            new ProcessRequest("wsl.exe", arguments),
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return ClaudeCodeHomeResolution.Failure(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "無法從指定的 WSL 環境解析 Claude Code 資料目錄。"
                    : result.StandardError.Trim());
        }

        return ClaudeCodeHomeResolution.Success(result.StandardOutput.Trim());
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
