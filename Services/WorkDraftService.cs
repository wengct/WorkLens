using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class WorkDraftService(IDbContextFactory<WorkLensDbContext> factory,
    WorkLogService workLogs, ManualSourceService manualSources)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const string ConflictMessage = "草稿已在其他分頁更新或儲存。你的輸入仍保留；請先複製內容，再重新載入最新草稿。";

    public async Task<WorkDraft?> GetAsync(string id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<IReadOnlyList<WorkDraft>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var drafts = await db.WorkDrafts.AsNoTracking().ToListAsync();
        return drafts.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<WorkDraft> SaveAsync(WorkDraftKind kind, DateOnly date, Guid? targetId,
        WorkDraftInput input, Guid? expectedVersion, string? baseVersion)
    {
        await using var db = await factory.CreateDbContextAsync();
        var id = WorkDraft.Key(kind, date, targetId);
        var draft = await db.WorkDrafts.SingleOrDefaultAsync(x => x.Id == id);
        if (draft?.Version != expectedVersion) throw new InvalidOperationException(ConflictMessage);
        if (draft is null)
        {
            draft = new WorkDraft { Id = id, Kind = kind, Date = date, TargetId = targetId, BaseVersion = baseVersion };
            db.WorkDrafts.Add(draft);
        }
        draft.Payload = JsonSerializer.Serialize(input, JsonOptions);
        draft.Version = Guid.NewGuid();
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { throw new InvalidOperationException(ConflictMessage); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
        { throw new InvalidOperationException(ConflictMessage); }
        return draft;
    }

    public async Task DeleteAsync(string id, Guid version)
    {
        await using var db = await factory.CreateDbContextAsync();
        var count = await db.WorkDrafts.Where(x => x.Id == id && x.Version == version).ExecuteDeleteAsync();
        if (count != 1) throw new InvalidOperationException(ConflictMessage);
    }

    public async Task PublishAsync(WorkDraft draft)
    {
        var input = JsonSerializer.Deserialize<WorkDraftInput>(draft.Payload, JsonOptions)!;
        if (!DateOnly.TryParseExact(input.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ArgumentException("請輸入有效日期。");
        Guid? project = Guid.TryParse(input.ProjectId, out var projectId) ? projectId : null;
        var commit = new WorkDraftCommit(draft.Id, draft.Version);
        if (draft.Kind is WorkDraftKind.WorkCreate or WorkDraftKind.WorkEdit)
        {
            if (!double.TryParse(input.Hours, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
                throw new ArgumentException("請輸入有效時數。");
            if (draft.Kind == WorkDraftKind.WorkCreate)
                await workLogs.AddAsync(date, hours, input.Content, project, input.Title, draftCommit: commit);
            else
                await workLogs.UpdateAsync(draft.TargetId!.Value, date, hours, input.Content, project, input.Title, draftCommit: commit);
        }
        else if (draft.Kind == WorkDraftKind.ManualCreate)
            await manualSources.AddAsync(date, input.Content, project, input.Title, draftCommit: commit);
        else
            await manualSources.UpdateAsync(draft.TargetId!.Value, input.Content, project, input.Title, draftCommit: commit);
    }

    public async Task<string?> GetBaseVersionAsync(WorkDraftKind kind, Guid? targetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await ReadBaseVersionAsync(db, kind, targetId, default);
    }

    public async Task<(WorkDraftInput Input, string Version)?> GetOriginalAsync(WorkDraftKind kind, Guid targetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (kind == WorkDraftKind.WorkEdit)
        {
            var entry = await db.WorkEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId);
            if (entry is null) return null;
            return (new WorkDraftInput
            {
                Date = entry.WorkDate.ToString("yyyy-MM-dd"), Hours = entry.Hours.ToString(CultureInfo.InvariantCulture),
                ProjectId = entry.ProjectId?.ToString() ?? "", Title = entry.Title, Content = entry.WorkContent
            }, entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        }
        var source = await db.SourceEvidence.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId && x.Kind == EvidenceKind.Manual);
        if (source is null) return null;
        return (new WorkDraftInput
        {
            Date = DateOnly.FromDateTime(source.OccurredAt.LocalDateTime).ToString("yyyy-MM-dd"),
            ProjectId = source.ProjectId?.ToString() ?? "", Title = source.Title, Content = source.CommitMessage
        }, source.LastObservedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static async Task<string?> ReadBaseVersionAsync(WorkLensDbContext db, WorkDraftKind kind,
        Guid? targetId, CancellationToken cancellationToken)
    {
        if (targetId is null) return null;
        if (kind == WorkDraftKind.WorkEdit)
        {
            var entry = await db.WorkEntries.SingleOrDefaultAsync(x => x.Id == targetId, cancellationToken);
            return entry?.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        }
        var source = await db.SourceEvidence.SingleOrDefaultAsync(x => x.Id == targetId && x.Kind == EvidenceKind.Manual, cancellationToken);
        return source?.LastObservedAt.ToString("O", CultureInfo.InvariantCulture);
    }

    // Called before mutation; SaveChanges atomically writes the record and consumes the draft.
    internal static async Task ConsumeAsync(WorkLensDbContext db, WorkDraftCommit? commit,
        WorkDraftKind kind, Guid? targetId, CancellationToken cancellationToken)
    {
        if (commit is null) return;
        var draft = await db.WorkDrafts.SingleOrDefaultAsync(x => x.Id == commit.Id, cancellationToken);
        if (draft is null || draft.Version != commit.Version || draft.Kind != kind || draft.TargetId != targetId)
            throw new InvalidOperationException(ConflictMessage);
        if (targetId is not null)
        {
            var current = await ReadBaseVersionAsync(db, kind, targetId, cancellationToken);
            if (current is null || current != draft.BaseVersion)
                throw new InvalidOperationException("原紀錄已變更或刪除，草稿仍保留。請先複製草稿內容，再重新載入原紀錄。");
        }
        db.WorkDrafts.Remove(draft);
    }
}
