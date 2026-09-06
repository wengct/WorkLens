using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Domain;

namespace WorkLens.Services;

/// <summary>
/// Reads the persisted VS Code chat session snapshot/journal files. VS Code
/// has used both JSON snapshots and append-only JSONL operation logs, so the
/// adapter replays the latter before extracting the visible conversation.
/// </summary>
public sealed class VsCodeCopilotSourceAdapter : IActivitySourceAdapter
{
    private const string RepositoryKey = "vscode-copilot-session";
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private readonly IVsCodeCopilotStorageResolver storageResolver;
    private readonly ActivitySourceType sourceType;

    public VsCodeCopilotSourceAdapter(ProcessRunner processRunner, ActivitySourceType sourceType)
        : this(new VsCodeCopilotStorageResolver(processRunner), sourceType)
    {
    }

    public VsCodeCopilotSourceAdapter(
        IVsCodeCopilotStorageResolver storageResolver,
        ActivitySourceType sourceType)
    {
        this.storageResolver = storageResolver;
        this.sourceType = sourceType;
    }

    public string SourceType => sourceType.ToString();
    public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

    public async Task<SourceValidationResult> ValidateAsync(
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        if (sourceType == ActivitySourceType.MacOsVsCodeCopilot && !OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "macOS VS Code Copilot 來源只能在 macOS 上執行。");
        }

        if (sourceType == ActivitySourceType.WindowsVsCodeCopilot && OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "Windows VS Code Copilot 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslVsCodeCopilot && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "WSL VS Code Copilot 來源只能在 Windows 上執行。");
        }

        var resolution = await storageResolver.ResolveAsync(sourceType, source, cancellationToken);
        if (!resolution.Succeeded)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                resolution.Error ?? "無法解析 VS Code Copilot 儲存位置。");
        }

        try
        {
            var pathStatuses = resolution.Paths
                .Select(path => (Path: path, Status: SourcePathProbe.CheckDirectory(path)))
                .ToList();
            var existingRoots = pathStatuses
                .Where(item => item.Status == SourcePathStatus.Readable)
                .Select(item => item.Path)
                .ToList();
            var details = pathStatuses
                .Select(item => SourcePathProbe.Describe(item.Path, item.Status))
                .ToList();
            if (existingRoots.Count == 0)
            {
                var unavailableSummary = pathStatuses.Any(item => item.Status == SourcePathStatus.AccessDenied)
                    ? "VS Code workspaceStorage 資料夾存取遭拒。"
                    : pathStatuses.Any(item => item.Status == SourcePathStatus.Unreadable)
                        ? "VS Code workspaceStorage 資料夾無法讀取。"
                        : "找不到 VS Code workspaceStorage 資料夾。";
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    unavailableSummary,
                    details.ToArray());
            }

            var scanDiagnostics = new List<string>();
            var hasSessionFiles = existingRoots
                .SelectMany(root => EnumerateChatFiles(root, scanDiagnostics))
                .Any();
            details.AddRange(scanDiagnostics);
            details.Add(!hasSessionFiles
                ? "workspaceStorage 存在，但目前沒有 chatSessions 對話檔案。"
                : "已找到 chatSessions 對話檔案。");
            var hasPathErrors = pathStatuses.Any(item => item.Status != SourcePathStatus.Readable);
            return SourceValidationResult.Valid(
                scanDiagnostics.Count > 0 || hasPathErrors
                    ? "VS Code Copilot 對話資料可讀取，但部分資料夾無法讀取。"
                    : !hasSessionFiles
                    ? "VS Code 儲存位置可讀取，但目前沒有對話檔案。"
                    : "VS Code Copilot 對話資料可讀取。",
                details.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                "VS Code Copilot 對話資料無法讀取。",
                exception.Message);
        }
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        var resolution = await storageResolver.ResolveAsync(sourceType, request.Source, cancellationToken);
        if (!resolution.Succeeded)
        {
            batch.Warnings.Add(resolution.Error ?? "無法解析 VS Code Copilot 儲存位置。");
            return batch;
        }

        var scanStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var scanDiagnostics = new List<string>();
            var allFiles = new List<FileInfo>();
            var readableRootCount = 0;
            foreach (var root in resolution.Paths)
            {
                var status = SourcePathProbe.CheckDirectory(root);
                if (status == SourcePathStatus.Readable)
                {
                    readableRootCount++;
                    allFiles.AddRange(EnumerateChatFiles(root, scanDiagnostics));
                }
                else
                {
                    batch.Warnings.Add(SourcePathProbe.Describe(root, status));
                }
            }
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

                    var identity = $"{parsed.SourceFormat}:{CopilotSourceIdentity.Fingerprint(parsed.RootPath)}:{parsed.WorkspaceId}:{parsed.SessionId}";
                    if (!sessions.TryGetValue(identity, out var current) ||
                        parsed.UpdatedAt > current.UpdatedAt ||
                        (parsed.UpdatedAt == current.UpdatedAt && parsed.IsComplete && !current.IsComplete))
                    {
                        sessions[identity] = parsed;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
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

                var metadata = new CopilotSessionMetadata
                {
                    SessionId = session.SessionId,
                    UpdatedAt = session.UpdatedAt,
                    Cwd = session.Cwd,
                    Platform = sourceType.ToString(),
                    SourceFormat = Path.GetExtension(session.FilePath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                        ? "vscode-chat-session-json"
                        : "vscode-chat-session-jsonl",
                    Client = "GitHub Copilot Chat",
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
                    RepositoryPath = session.Cwd ?? session.RootPath,
                    Environment = sourceType.ToString(),
                    Kind = EvidenceKind.CopilotSession,
                    ExternalKey = $"session:{sourceFormat}:{CopilotSourceIdentity.Fingerprint(session.RootPath)}:{session.WorkspaceId}:{session.SessionId}",
                    Title = session.Title,
                    CommitMessage = userContext,
                    OccurredAt = session.OccurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeCopilotMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                });
            }

            batch.SuccessfulRepositories = readableRootCount > 0 ? 1 : 0;
            if (request.UpdateCheckpoint)
            {
                batch.CheckpointJson = JsonSerializer.Serialize(new VsCodeCheckpoint
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

    private static IReadOnlyList<FileInfo> EnumerateChatFiles(string root, ICollection<string>? diagnostics = null)
    {
        var files = new List<FileInfo>();
        try
        {
            foreach (var chatDirectory in Directory.EnumerateDirectories(root, "chatSessions", SearchOption.AllDirectories))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(chatDirectory, "*", SearchOption.TopDirectoryOnly)
                                 .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                                path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
                    {
                        files.Add(new FileInfo(path));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    diagnostics?.Add($"{chatDirectory}：無法讀取 chatSessions：{exception.Message}");
                    continue;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics?.Add($"{root}：無法列出 chatSessions：{exception.Message}");
        }

        return files;
    }

    private static ParsedSession? ParseSession(FileInfo file, List<string> warnings)
    {
        var rootPath = FindWorkspaceStorageRoot(file.FullName);
        var workspaceId = file.Directory?.Parent?.Name ?? "unknown";
        var complete = true;
        JsonNode? sessionRoot = null;
        file.Refresh();
        var initialLength = file.Length;
        var initialWriteTime = file.LastWriteTimeUtc;
        if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            sessionRoot = JsonNode.Parse(ReadSharedText(file.FullName));
        }
        else
        {
            var rows = new List<JsonObject>();
            foreach (var line in ReadSharedLines(file.FullName))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(line) is JsonObject row)
                    {
                        rows.Add(row);
                    }
                    else
                    {
                        complete = false;
                    }
                }
                catch (JsonException)
                {
                    complete = false;
                }
            }

            if (rows.Count == 1 && rows[0]["kind"] is null)
            {
                sessionRoot = rows[0];
            }
            else if (rows.Count > 0 && rows[0]["kind"] is null)
            {
                sessionRoot = rows[0]["v"]?.DeepClone() ?? rows[0].DeepClone();
                sessionRoot = ReplayOperations(rows.Skip(1), ref complete, sessionRoot);
            }
            else
            {
                sessionRoot = ReplayOperations(rows, ref complete);
            }
        }

        if (HasFileChanged(file, initialLength, initialWriteTime))
        {
            complete = false;
        }

        if (sessionRoot is not JsonObject root)
        {
            warnings.Add($"{file.Name}：找不到 VS Code chat session JSON 根物件，已略過。格式可能尚未支援。");
            return null;
        }

        if (!IsCopilotSession(root))
        {
            warnings.Add($"{file.Name}：找不到明確的 GitHub Copilot 識別欄位，已略過。檔案不是可辨識的 Copilot Chat session。");
            return null;
        }

        var requests = root["requests"] as JsonArray;
        if (requests is null)
        {
            warnings.Add($"{file.Name}：找不到 VS Code chat requests 陣列，已略過。格式可能尚未支援。");
            return null;
        }

        var sessionId = FirstString(root, "sessionId", "session_id", "id") ??
                        Path.GetFileNameWithoutExtension(file.Name);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var messages = new List<CopilotSessionMessage>();
        DateTimeOffset? createdAt = FirstTimestamp(root, "creationDate", "createdAt", "created_at");
        DateTimeOffset? firstMessageAt = null;
        DateTimeOffset? lastRecordAt = null;
        var ordinal = 0;
        foreach (var requestNode in requests)
        {
            ordinal++;
            if (requestNode is not JsonObject request)
            {
                continue;
            }

            var explicitTimestamp = FirstTimestamp(request, "timestamp", "createdAt", "created_at");
            var timestamp = explicitTimestamp ??
                            createdAt ??
                            new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
            lastRecordAt = lastRecordAt is null || timestamp > lastRecordAt ? timestamp : lastRecordAt;
            var requestId = FirstString(request, "requestId", "id") ?? $"request-{ordinal}";
            var prompt = ReadVisibleText(request["message"]);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                if (explicitTimestamp is not null &&
                    (firstMessageAt is null || explicitTimestamp.Value < firstMessageAt.Value))
                {
                    firstMessageAt = explicitTimestamp;
                }
                messages.Add(new CopilotSessionMessage
                {
                    Id = $"user:{requestId}",
                    Role = "user",
                    Timestamp = timestamp,
                    Text = prompt
                });
            }

            var response = ReadResponseText(request["response"]);
            if (!string.IsNullOrWhiteSpace(response))
            {
                if (explicitTimestamp is not null &&
                    (firstMessageAt is null || explicitTimestamp.Value < firstMessageAt.Value))
                {
                    firstMessageAt = explicitTimestamp;
                }
                messages.Add(new CopilotSessionMessage
                {
                    Id = $"assistant:{requestId}",
                    Role = "assistant",
                    Timestamp = timestamp,
                    Text = response
                });
            }
        }

        var visibleMessages = messages
            .GroupBy(message => message.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(message => message.Text.Length).First())
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
            sessionId.Trim(),
            BuildTitle(firstUser?.Text ?? FirstString(root, "customTitle", "sessionId") ?? sessionId),
            occurredAt.Value,
            updatedAt,
            FirstString(root, "workingDirectory", "workspaceFolder", "cwd"),
            rootPath,
            workspaceId,
            file.FullName,
            complete,
            visibleMessages,
            file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? "vscode-chat-session-json"
                : "vscode-chat-session-jsonl");
    }

    private static bool IsCopilotSession(JsonObject root)
    {
        var responder = FirstString(root, "responderUsername", "responder", "provider");
        if (ContainsCopilot(responder))
        {
            return true;
        }

        if (root["requests"] is not JsonArray requests)
        {
            return false;
        }

        return requests
            .OfType<JsonObject>()
            .Any(request => ContainsCopilot(FirstString(request, "agent", "agentId", "provider")) ||
                           request["agent"] is JsonObject agent &&
                           ContainsCopilot(FirstString(agent, "id", "name", "extensionId", "extension")));
    }

    private static bool ContainsCopilot(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains("copilot", StringComparison.OrdinalIgnoreCase);

    private static string ReadResponseText(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return string.Join(
                Environment.NewLine,
                array
                    .Where(IsVisibleAssistantPart)
                    .Select(ReadVisibleText)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return IsVisibleAssistantPart(node) ? ReadVisibleText(node) : string.Empty;
    }

    private static JsonNode? ReplayOperations(
        IEnumerable<JsonObject> rows,
        ref bool complete,
        JsonNode? initialRoot = null)
    {
        JsonNode? root = initialRoot;
        foreach (var row in rows)
        {
            var kind = TryGetOperationKind(row["kind"]);
            try
            {
                switch (kind)
                {
                    case 0:
                        root = row["v"]?.DeepClone();
                        break;
                    case 1:
                        if (root is null || row["k"] is not JsonArray setPath)
                        {
                            complete = false;
                            continue;
                        }
                        if (!ApplySet(root, setPath, row["v"]?.DeepClone()))
                        {
                            complete = false;
                        }
                        break;
                    case 2:
                        if (root is null || row["k"] is not JsonArray pushPath)
                        {
                            complete = false;
                            continue;
                        }
                        if (!ApplyPush(root, pushPath, row["v"]?.DeepClone(), row["i"]?.GetValue<int>()))
                        {
                            complete = false;
                        }
                        break;
                    case 3:
                        if (root is null || row["k"] is not JsonArray deletePath)
                        {
                            complete = false;
                            continue;
                        }
                        if (!ApplyDelete(root, deletePath))
                        {
                            complete = false;
                        }
                        break;
                    default:
                        complete = false;
                        break;
                }
            }
            catch (Exception)
            {
                complete = false;
            }
        }

        return root;
    }

    private static int TryGetOperationKind(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (node is JsonValue textValue &&
            textValue.TryGetValue<string>(out var text) &&
            (int.TryParse(text, out number) ||
             (number = text.Trim().ToLowerInvariant() switch
             {
                 "init" or "initialize" or "snapshot" => 0,
                 "set" or "update" => 1,
                 "push" or "append" or "insert" => 2,
                 "delete" or "remove" => 3,
                 _ => -1
             }) >= 0))
        {
            return number;
        }

        return -1;
    }

    private static bool ApplySet(JsonNode root, JsonArray path, JsonNode? value)
    {
        if (path.Count == 0)
        {
            return false;
        }

        var parent = FindParent(root, path, out var final);
        if (parent is JsonObject objectParent && final is string property)
        {
            objectParent[property] = value;
            return true;
        }
        if (parent is JsonArray arrayParent && final is int index && index >= 0)
        {
            while (arrayParent.Count <= index)
            {
                arrayParent.Add(null);
            }
            arrayParent[index] = value;
            return true;
        }

        return false;
    }

    private static bool ApplyPush(JsonNode root, JsonArray path, JsonNode? value, int? index)
    {
        var target = FindNode(root, path) as JsonArray;
        if (target is null)
        {
            return false;
        }

        var values = value is JsonArray array
            ? array.Select(item => item?.DeepClone()).ToList()
            : [value];
        var insertAt = index ?? target.Count;
        if (insertAt < 0 || insertAt > target.Count)
        {
            return false;
        }

        foreach (var item in values)
        {
            target.Insert(insertAt++, item);
        }

        return true;
    }

    private static bool ApplyDelete(JsonNode root, JsonArray path)
    {
        if (path.Count == 0)
        {
            return false;
        }

        var parent = FindParent(root, path, out var final);
        if (parent is JsonObject objectParent && final is string property)
        {
            return objectParent.Remove(property);
        }
        if (parent is JsonArray arrayParent && final is int index && index >= 0 && index < arrayParent.Count)
        {
            arrayParent.RemoveAt(index);
            return true;
        }

        return false;
    }

    private static JsonNode? FindNode(JsonNode root, JsonArray path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = segment switch
            {
                JsonValue value when value.TryGetValue<string>(out var property) && current is JsonObject obj => obj[property],
                JsonValue value when value.TryGetValue<int>(out var index) && current is JsonArray array && index >= 0 && index < array.Count => array[index],
                _ => null
            };
        }
        return current;
    }

    private static JsonNode? FindParent(JsonNode root, JsonArray path, out object final)
    {
        final = string.Empty;
        if (path.Count == 0)
        {
            return null;
        }

        var parentPath = new JsonArray(path.Take(path.Count - 1).Select(item => item?.DeepClone()).ToArray());
        var parent = FindNode(root, parentPath);
        var last = path[^1];
        final = last is JsonValue value && value.TryGetValue<int>(out var index) ? index : last?.GetValue<string>() ?? string.Empty;
        return parent;
    }

    private static string? FirstString(JsonObject? objectNode, params string[] names)
    {
        if (objectNode is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (objectNode[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (objectNode[name] is JsonObject nested)
            {
                var nestedText = FirstString(nested, "id", "name", "text", "value", "extensionId", "extension");
                if (!string.IsNullOrWhiteSpace(nestedText))
                {
                    return nestedText;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? FirstTimestamp(JsonObject objectNode, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetTimestamp(objectNode[name], out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static bool TryGetTimestamp(JsonNode? node, out DateTimeOffset value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var number))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeMilliseconds(number);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                value = default;
                return false;
            }
        }

        if (node is JsonValue stringValue && stringValue.TryGetValue<string>(out var text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string ReadVisibleText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (node is JsonObject objectNode)
        {
            if (!IsVisibleAssistantPart(objectNode))
            {
                return string.Empty;
            }

            foreach (var name in new[] { "text", "content", "value", "markdown" })
            {
                var result = ReadVisibleText(objectNode[name]);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }
            return string.Empty;
        }

        if (node is JsonArray array)
        {
            return string.Join(
                Environment.NewLine,
                array.Select(ReadVisibleText).Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return string.Empty;
    }

    private static bool IsVisibleAssistantPart(JsonNode? node)
    {
        if (node is not JsonObject objectNode)
        {
            return true;
        }

        foreach (var name in new[] { "kind", "type", "category" })
        {
            if (objectNode[name] is JsonValue value &&
                value.TryGetValue<string>(out var marker) &&
                (marker.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
                 marker.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                 marker.Contains("invocation", StringComparison.OrdinalIgnoreCase) ||
                 marker.Contains("inlineReference", StringComparison.OrdinalIgnoreCase) ||
                 marker.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
                 marker.Contains("attachment", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasFileChanged(FileInfo file, long initialLength, DateTime initialWriteTime)
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

    private static string FindWorkspaceStorageRoot(string path)
    {
        var directory = new DirectoryInfo(path).Parent;
        while (directory is not null && !directory.Name.Equals("workspaceStorage", StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Path.GetDirectoryName(path) ?? string.Empty;
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

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static VsCodeCheckpoint DeserializeCheckpoint(string? json)
    {
        try
        {
            var checkpoint = string.IsNullOrWhiteSpace(json)
                ? new VsCodeCheckpoint()
                : JsonSerializer.Deserialize<VsCodeCheckpoint>(json) ?? new VsCodeCheckpoint();
            checkpoint.RetryPaths ??= [];
            return checkpoint;
        }
        catch (JsonException)
        {
            return new VsCodeCheckpoint();
        }
    }

    private sealed class VsCodeCheckpoint
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
        string RootPath,
        string WorkspaceId,
        string FilePath,
        bool IsComplete,
        List<CopilotSessionMessage> Messages,
        string SourceFormat);
}

public interface IVsCodeCopilotStorageResolver
{
    Task<VsCodeStorageResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken);
}

public sealed record VsCodeStorageResolution(bool Succeeded, IReadOnlyList<string> Paths, string? Error)
{
    public static VsCodeStorageResolution Success(IEnumerable<string> paths) =>
        new(true, paths.ToList(), null);

    public static VsCodeStorageResolution Failure(string error) =>
        new(false, [], error);
}

public sealed class VsCodeCopilotStorageResolver(IProcessRunner processRunner) : IVsCodeCopilotStorageResolver
{
    public async Task<VsCodeStorageResolution> ResolveAsync(
        ActivitySourceType sourceType,
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeVsCodeCopilot(source.SettingsJson);
        var configured = settings.WorkspaceStoragePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceType == ActivitySourceType.WslVsCodeCopilot)
        {
            if (string.IsNullOrWhiteSpace(settings.Distro))
            {
                return VsCodeStorageResolution.Failure("WSL VS Code Copilot 來源必須指定 Linux 環境名稱，例如 Ubuntu。");
            }

            if (configured.Count == 0)
            {
                configured = [
                    "${XDG_CONFIG_HOME:-$HOME/.config}/Code/User/workspaceStorage",
                    "${XDG_CONFIG_HOME:-$HOME/.config}/Code - Insiders/User/workspaceStorage"
                ];
            }

            var resolved = new List<string>();
            foreach (var path in configured)
            {
                IReadOnlyList<string> arguments = path.Contains("${XDG_CONFIG_HOME", StringComparison.Ordinal) ||
                                                   path.StartsWith("~/", StringComparison.Ordinal)
                    ? ["-d", settings.Distro, "--", "sh", "-lc", BuildWslPathCommand(path)]
                    : ["-d", settings.Distro, "--", "wslpath", "-w", "--", path];
                var result = await processRunner.RunAsync(
                    new ProcessRequest("wsl.exe", arguments),
                    timeout: TimeSpan.FromSeconds(15),
                    cancellationToken: cancellationToken);
                if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    return VsCodeStorageResolution.Failure(
                        string.IsNullOrWhiteSpace(result.StandardError)
                            ? $"無法從 WSL {settings.Distro} 解析 VS Code 儲存位置。"
                            : result.StandardError.Trim());
                }
                resolved.Add(result.StandardOutput.Trim());
            }
            return VsCodeStorageResolution.Success(resolved);
        }

        if (configured.Count == 0)
        {
            configured = DefaultPaths(sourceType);
        }

        try
        {
            return VsCodeStorageResolution.Success(configured.Select(ExpandLocalPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return VsCodeStorageResolution.Failure(exception.Message);
        }
    }

    private static List<string> DefaultPaths(ActivitySourceType sourceType)
    {
        if (sourceType == ActivitySourceType.MacOsVsCodeCopilot)
        {
            var basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
            return [
                Path.Combine(basePath, "Code", "User", "workspaceStorage"),
                Path.Combine(basePath, "Code - Insiders", "User", "workspaceStorage")
            ];
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return [
            Path.Combine(appData, "Code", "User", "workspaceStorage"),
            Path.Combine(appData, "Code - Insiders", "User", "workspaceStorage")
        ];
    }

    private static string ExpandLocalPath(string value)
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

        return Path.GetFullPath(expanded);
    }

    private static string BuildWslPathCommand(string path)
    {
        const string xdgPrefix = "${XDG_CONFIG_HOME:-$HOME/.config}";
        if (path.StartsWith(xdgPrefix, StringComparison.Ordinal))
        {
            return $"wslpath -w -- \"{xdgPrefix}\"{QuoteShell(path[xdgPrefix.Length..])}";
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return $"wslpath -w -- \"$HOME\"{QuoteShell("/" + path[2..])}";
        }

        return $"wslpath -w -- {QuoteShell(path)}";
    }

    private static string QuoteShell(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
