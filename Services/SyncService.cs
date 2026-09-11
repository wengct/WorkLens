using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed record SyncStatus(bool Enabled, Guid DeviceId, string DeviceName, string? SyncSpaceId, string? FolderPath, int PendingEvents, DateTimeOffset? LastExportedAt, DateTimeOffset? LastImportedAt, string? LastError);

public sealed class SyncService(IDbContextFactory<WorkLensDbContext> factory, ILogger<SyncService> logger)
{
    private const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<SyncStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var config = await GetConfigAsync(db, ct);
        return Status(config, await db.SyncOutboxEvents.CountAsync(x => x.PublishedAt == null, ct));
    }

    public async Task<SyncStatus> ConfigureAsync(string folderPath, string? deviceName, bool createSpace, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentException("同步資料夾不可空白。", nameof(folderPath));
        var folder = Path.GetFullPath(AppPaths.ExpandPath(folderPath));
        Directory.CreateDirectory(folder);
        var markerPath = Path.Combine(folder, ".worklens-sync-space.json");
        string spaceId;
        if (File.Exists(markerPath))
        {
            var marker = JsonSerializer.Deserialize<Marker>(await File.ReadAllTextAsync(markerPath, ct), Json) ?? throw new InvalidOperationException("同步資料夾識別檔無法讀取。");
            if (marker.ProtocolVersion != ProtocolVersion || string.IsNullOrWhiteSpace(marker.SyncSpaceId)) throw new InvalidOperationException("同步資料夾使用不支援的 WorkLens 同步協定。");
            spaceId = marker.SyncSpaceId;
        }
        else
        {
            if (!createSpace) throw new InvalidOperationException("找不到同步空間識別檔；請等待雲端同步完成，或選擇建立新的同步空間。");
            spaceId = Guid.NewGuid().ToString("N");
            var temporary = markerPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Marker(ProtocolVersion, spaceId), Json), new UTF8Encoding(false), ct);
            File.Move(temporary, markerPath);
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var config = await GetConfigAsync(db, ct);
        if (!string.IsNullOrWhiteSpace(config.SyncSpaceId) && config.SyncSpaceId != spaceId) throw new InvalidOperationException("此安裝已加入另一個同步空間；請先停用同步並確認資料夾。");
        config.SyncSpaceId = spaceId;
        config.FolderPath = folder;
        config.DeviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
        config.Enabled = true;
        config.LastError = null;
        await db.SaveChangesAsync(ct);
        return Status(config, await db.SyncOutboxEvents.CountAsync(x => x.PublishedAt == null, ct));
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        (await GetConfigAsync(db, ct)).Enabled = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task<SyncStatus> SyncAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var config = await GetConfigAsync(db, ct);
            if (!config.Enabled || string.IsNullOrWhiteSpace(config.FolderPath) || string.IsNullOrWhiteSpace(config.SyncSpaceId)) return Status(config, await db.SyncOutboxEvents.CountAsync(x => x.PublishedAt == null, ct));
            try
            {
                await QueueAsync(db, ct);
                await db.SaveChangesAsync(ct);
                await PublishAsync(db, config, ct);
                await ImportAsync(db, config, ct);
                config.LastError = null;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                config.LastError = exception.Message;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(exception, "跨電腦同步失敗");
            }
            return Status(config, await db.SyncOutboxEvents.CountAsync(x => x.PublishedAt == null, ct));
        }
        finally { gate.Release(); }
    }

    private static async Task<SyncConfiguration> GetConfigAsync(WorkLensDbContext db, CancellationToken ct)
    {
        var config = await db.SyncConfigurations.SingleOrDefaultAsync(x => x.Id == SyncConfiguration.SingletonId, ct);
        if (config is not null) return config;
        config = new SyncConfiguration();
        db.SyncConfigurations.Add(config);
        return config;
    }

    private static SyncStatus Status(SyncConfiguration config, int pending) => new(config.Enabled, config.DeviceId, config.DeviceName, config.SyncSpaceId, config.FolderPath, pending, config.LastExportedAt, config.LastImportedAt, config.LastError);

    private static async Task QueueAsync(WorkLensDbContext db, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var current = new Dictionary<(SyncEntityKind Kind, Guid Id), string>();
        foreach (var entry in await db.WorkEntries.AsNoTracking().ToListAsync(ct))
            current[(SyncEntityKind.WorkEntry, entry.Id)] = JsonSerializer.Serialize(new WorkPayload(entry.Id, entry.WorkDate, entry.Hours, entry.Title, entry.WorkContent, entry.CreatedAt, entry.UpdatedAt), Json);
        foreach (var evidence in await db.SourceEvidence.AsNoTracking().ToListAsync(ct))
        {
            var projectName = evidence.ProjectId is { } projectId && projects.TryGetValue(projectId, out var name) ? name : null;
            current[(SyncEntityKind.SourceEvidence, evidence.Id)] = JsonSerializer.Serialize(new SourcePayload(evidence.Id, evidence.SourceId, evidence.RepositoryKey, evidence.RepositoryPath, evidence.Environment, evidence.Kind, evidence.ExternalKey, evidence.Title, evidence.CommitMessage, evidence.OccurredAt, evidence.CommitHash, evidence.ParentHashes, evidence.PatchId, evidence.Branch, evidence.MetadataJson, evidence.ReachabilityStatus, evidence.FirstObservedAt, evidence.LastObservedAt, projectName), Json);
        }

        var states = await db.SyncEntityStates.ToListAsync(ct);
        foreach (var pair in current)
        {
            var state = states.SingleOrDefault(x => x.EntityKind == pair.Key.Kind.ToString() && x.EntityId == pair.Key.Id);
            var contentHash = Hash(pair.Value);
            if (state?.IsDeleted == false && state.ContentHash == contentHash) continue;
            if (state is null)
            {
                state = new SyncEntityState { EntityKind = pair.Key.Kind.ToString(), EntityId = pair.Key.Id };
                db.SyncEntityStates.Add(state);
                states.Add(state);
            }
            state.Version++;
            state.ContentHash = contentHash;
            state.IsDeleted = false;
            db.SyncOutboxEvents.Add(new SyncOutboxEvent { EntityKind = pair.Key.Kind, EntityId = pair.Key.Id, Version = state.Version, Operation = SyncOperation.Upsert, PayloadJson = pair.Value });
        }
        foreach (var state in states.Where(x => !x.IsDeleted && !current.ContainsKey((Enum.Parse<SyncEntityKind>(x.EntityKind), x.EntityId))))
        {
            var kind = Enum.Parse<SyncEntityKind>(state.EntityKind);
            state.Version++;
            state.IsDeleted = true;
            state.ContentHash = string.Empty;
            db.SyncOutboxEvents.Add(new SyncOutboxEvent { EntityKind = kind, EntityId = state.EntityId, Version = state.Version, Operation = SyncOperation.Delete });
        }
    }

    private static async Task PublishAsync(WorkLensDbContext db, SyncConfiguration config, CancellationToken ct)
    {
        var events = (await db.SyncOutboxEvents.Where(x => x.PublishedAt == null).ToListAsync(ct)).OrderBy(x => x.OccurredAt).ToList();
        if (events.Count == 0) return;
        var text = string.Join("\n", events.Select(x => JsonSerializer.Serialize(new SyncEvent(ProtocolVersion, config.SyncSpaceId!, x.Id, config.DeviceId, config.DeviceName, x.EntityKind, x.EntityId, x.Version, x.Operation, x.PayloadJson), Json))) + "\n";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var hash = Hash(bytes);
        var directory = Path.Combine(config.FolderPath!, "devices", config.DeviceId.ToString("N"), "batches");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{hash}.jsonl");
        var temporaryPath = finalPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
        File.Move(temporaryPath, finalPath);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in events) item.PublishedAt = now;
        config.LastExportedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static async Task ImportAsync(WorkLensDbContext db, SyncConfiguration config, CancellationToken ct)
    {
        var devices = Path.Combine(config.FolderPath!, "devices");
        if (!Directory.Exists(devices)) return;
        var known = (await db.SyncProcessedBatches.AsNoTracking().Where(x => x.SyncSpaceId == config.SyncSpaceId).Select(x => x.ContentHash).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(devices, "*.jsonl", SearchOption.AllDirectories))
        {
            var expectedHash = FileHash(path);
            if (expectedHash is not null && known.Contains(expectedHash)) continue;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var actualHash = Hash(bytes);
            if (expectedHash is null || actualHash != expectedHash) continue;
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var item = JsonSerializer.Deserialize<SyncEvent>(line, Json) ?? throw new JsonException("同步事件無法讀取。");
                if (item.ProtocolVersion != ProtocolVersion || item.SyncSpaceId != config.SyncSpaceId || item.OriginDeviceId == config.DeviceId) continue;
                var eventHash = Hash(line);
                var processed = await db.SyncProcessedEvents.SingleOrDefaultAsync(x => x.Id == item.EventId, ct);
                if (processed is not null)
                {
                    if (processed.ContentHash != eventHash) throw new InvalidOperationException("偵測到相同同步事件 ID 的內容衝突。");
                    continue;
                }
                await ApplyAsync(db, item, ct);
                db.SyncProcessedEvents.Add(new SyncProcessedEvent { Id = item.EventId, ContentHash = eventHash });
            }
            db.SyncProcessedBatches.Add(new SyncProcessedBatch { Id = $"{config.SyncSpaceId}:{actualHash}", SyncSpaceId = config.SyncSpaceId!, ContentHash = actualHash });
            known.Add(actualHash);
        }
        config.LastImportedAt = DateTimeOffset.UtcNow;
    }

    private static async Task ApplyAsync(WorkLensDbContext db, SyncEvent item, CancellationToken ct)
    {
        if (item.EntityKind == SyncEntityKind.WorkEntry)
        {
            var row = await db.RemoteWorkEntries.SingleOrDefaultAsync(x => x.OriginDeviceId == item.OriginDeviceId && x.OriginEntityId == item.EntityId, ct);
            if (row is null) { row = new RemoteWorkEntry { Id = Projection(item.OriginDeviceId, item.EntityId), OriginDeviceId = item.OriginDeviceId, OriginEntityId = item.EntityId }; db.RemoteWorkEntries.Add(row); }
            if (row.Version >= item.Version) return;
            row.OriginDeviceName = item.OriginDeviceName; row.Version = item.Version; row.IsDeleted = item.Operation == SyncOperation.Delete;
            if (!row.IsDeleted)
            {
                var payload = JsonSerializer.Deserialize<WorkPayload>(item.PayloadJson, Json) ?? throw new JsonException("工作紀錄內容無效。");
                row.WorkDate = payload.WorkDate; row.Hours = payload.Hours; row.Title = payload.Title; row.WorkContent = payload.WorkContent; row.CreatedAt = payload.CreatedAt; row.UpdatedAt = payload.UpdatedAt;
            }
            return;
        }
        if (item.EntityKind == SyncEntityKind.SourceEvidence)
        {
            var row = await db.RemoteSourceEvidence.SingleOrDefaultAsync(x => x.OriginDeviceId == item.OriginDeviceId && x.OriginEntityId == item.EntityId, ct);
            if (row is null) { row = new RemoteSourceEvidence { Id = Projection(item.OriginDeviceId, item.EntityId), OriginDeviceId = item.OriginDeviceId, OriginEntityId = item.EntityId }; db.RemoteSourceEvidence.Add(row); }
            if (row.Version >= item.Version) return;
            row.OriginDeviceName = item.OriginDeviceName; row.Version = item.Version; row.IsDeleted = item.Operation == SyncOperation.Delete;
            if (!row.IsDeleted)
            {
                var payload = JsonSerializer.Deserialize<SourcePayload>(item.PayloadJson, Json) ?? throw new JsonException("來源佐證內容無效。");
                row.ProjectName = payload.ProjectName; row.SourceId = payload.SourceId; row.RepositoryKey = payload.RepositoryKey; row.RepositoryPath = payload.RepositoryPath; row.Environment = payload.Environment; row.Kind = payload.Kind; row.ExternalKey = payload.ExternalKey; row.Title = payload.Title; row.CommitMessage = payload.CommitMessage; row.OccurredAt = payload.OccurredAt; row.CommitHash = payload.CommitHash; row.ParentHashes = payload.ParentHashes; row.PatchId = payload.PatchId; row.Branch = payload.Branch; row.MetadataJson = payload.MetadataJson; row.ReachabilityStatus = payload.ReachabilityStatus; row.FirstObservedAt = payload.FirstObservedAt; row.LastObservedAt = payload.LastObservedAt;
            }
        }
    }

    private static string? FileHash(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('-');
        if (separator < 0) return null;
        var hash = name[(separator + 1)..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
    }
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static Guid Projection(Guid device, Guid entity) => new(MD5.HashData(Encoding.UTF8.GetBytes($"{device:N}:{entity:N}")));

    private sealed record Marker(int ProtocolVersion, string SyncSpaceId);
    private sealed record SyncEvent(int ProtocolVersion, string SyncSpaceId, Guid EventId, Guid OriginDeviceId, string OriginDeviceName, SyncEntityKind EntityKind, Guid EntityId, int Version, SyncOperation Operation, string PayloadJson);
    private sealed record WorkPayload(Guid Id, DateOnly WorkDate, double Hours, string Title, string WorkContent, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record SourcePayload(Guid Id, Guid SourceId, string RepositoryKey, string RepositoryPath, string Environment, EvidenceKind Kind, string ExternalKey, string Title, string CommitMessage, DateTimeOffset OccurredAt, string? CommitHash, string? ParentHashes, string? PatchId, string? Branch, string MetadataJson, CommitReachabilityStatus ReachabilityStatus, DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt, string? ProjectName);
}

public sealed class SyncHostedService(IServiceScopeFactory scopes, ILogger<SyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SyncService>().SyncAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "跨電腦同步背景工作失敗"); }
        }
    }
}