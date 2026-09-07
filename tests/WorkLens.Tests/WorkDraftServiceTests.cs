using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class WorkDraftServiceTests
{
    private static readonly DateOnly Date = new(2026, 9, 7);
    private static readonly WorkDraftInput Input = new() { Date = "2026-09-07", Hours = "2.5", Content = "測試工作" };

    [Fact]
    public async Task Drafts_survive_new_service_instances_without_changing_work_or_reports()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input with { Hours = "未完成" }, null, null);
        await fixture.Drafts.SaveAsync(WorkDraftKind.ManualCreate, Date, null, Input, null, null);
        await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date.AddDays(-1), null, Input with { Date = "2026-09-06" }, null, null);
        var reloaded = await fixture.NewDraftService().GetAsync(first.Id);
        Assert.Equal(first.Payload, reloaded!.Payload);
        Assert.Equal(3, (await fixture.Drafts.ListAsync()).Count);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.WorkEntries.ToListAsync());
        Assert.Empty(await db.SourceEvidence.ToListAsync());
        Assert.False((await db.Reports.SingleAsync()).IsStale);
    }

    [Fact]
    public async Task Stale_tabs_cannot_overwrite_delete_or_publish_a_newer_draft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input, null, null);
        var updated = await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input with { Content = "新版" }, first.Version, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input, first.Version, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input, null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Drafts.DeleteAsync(first.Id, first.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Drafts.PublishAsync(first));
        Assert.Equal(updated.Payload, (await fixture.Drafts.GetAsync(first.Id))!.Payload);
    }

    [Theory]
    [InlineData(WorkDraftKind.WorkCreate)]
    [InlineData(WorkDraftKind.ManualCreate)]
    public async Task Publishing_consumes_draft_and_cannot_be_repeated(WorkDraftKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.Drafts.SaveAsync(kind, Date, null, Input, null, null);
        await fixture.Drafts.PublishAsync(draft);
        Assert.Null(await fixture.Drafts.GetAsync(draft.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Drafts.PublishAsync(draft));
        await using var db = fixture.Factory.CreateDbContext();
        Assert.True((await db.Reports.SingleAsync()).IsStale);
        Assert.Equal(kind == WorkDraftKind.WorkCreate ? 1 : 0, await db.WorkEntries.CountAsync());
        Assert.Equal(kind == WorkDraftKind.ManualCreate ? 1 : 0, await db.SourceEvidence.CountAsync());
    }

    [Theory]
    [InlineData(WorkDraftKind.WorkEdit, false)]
    [InlineData(WorkDraftKind.WorkEdit, true)]
    [InlineData(WorkDraftKind.ManualEdit, false)]
    [InlineData(WorkDraftKind.ManualEdit, true)]
    public async Task Changed_or_deleted_originals_preserve_edit_drafts(WorkDraftKind kind, bool delete)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = kind == WorkDraftKind.WorkEdit
            ? (await fixture.WorkLogs.AddAsync(Date, 1, "原紀錄")).Id
            : (await fixture.ManualSources.AddAsync(Date, "原紀錄")).Id;
        var version = await fixture.Drafts.GetBaseVersionAsync(kind, id);
        var draft = await fixture.Drafts.SaveAsync(kind, Date, id, Input, null, version);
        if (kind == WorkDraftKind.WorkEdit)
        {
            if (delete) await fixture.WorkLogs.DeleteAsync(id);
            else await fixture.WorkLogs.UpdateAsync(id, Date, 1, "其他分頁修改", null);
        }
        else
        {
            if (delete) await fixture.ManualSources.DeleteAsync(id);
            else await fixture.ManualSources.UpdateAsync(id, "其他分頁修改", null);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Drafts.PublishAsync(draft));
        Assert.NotNull(await fixture.Drafts.GetAsync(draft.Id));
    }

    [Theory]
    [InlineData(WorkDraftKind.WorkEdit)]
    [InlineData(WorkDraftKind.ManualEdit)]
    public async Task Editing_publishes_to_original_and_removes_only_its_draft(WorkDraftKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = kind == WorkDraftKind.WorkEdit
            ? (await fixture.WorkLogs.AddAsync(Date, 1, "原紀錄")).Id
            : (await fixture.ManualSources.AddAsync(Date, "原紀錄")).Id;
        var original = await fixture.Drafts.GetOriginalAsync(kind, id);
        var draft = await fixture.Drafts.SaveAsync(kind, Date, id, Input, null, original!.Value.Version);
        await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input, null, null);
        await fixture.Drafts.PublishAsync(draft);
        Assert.Single(await fixture.Drafts.ListAsync());
        Assert.Equal(Input.Content, (await fixture.Drafts.GetOriginalAsync(kind, id))!.Value.Input.Content);
    }

    [Fact]
    public async Task Validation_and_database_failures_keep_draft_and_roll_back_formal_write()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invalid = await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input with { Hours = "" }, null, null);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Drafts.PublishAsync(invalid));
        var valid = await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input, invalid.Version, null);
        await using var db = fixture.Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_draft_delete BEFORE DELETE ON WorkDrafts
            BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Drafts.PublishAsync(valid));
        Assert.NotNull(await fixture.Drafts.GetAsync(valid.Id));
        Assert.Empty(await db.WorkEntries.ToListAsync());
        Assert.False((await db.Reports.SingleAsync()).IsStale);
    }

    [Fact]
    public async Task Existing_databases_gain_draft_table_and_repeated_upgrade_preserves_drafts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TABLE WorkDrafts");
        var initializer = new DatabaseInitializer(fixture.Factory);
        await initializer.InitializeAsync();
        var draft = await fixture.Drafts.SaveAsync(WorkDraftKind.WorkCreate, Date, null, Input, null, null);
        await initializer.InitializeAsync();
        Assert.Equal(draft.Payload, (await fixture.Drafts.GetAsync(draft.Id))!.Payload);
    }

    private sealed class Fixture(SqliteConnection connection, Factory factory) : IAsyncDisposable
    {
        public Factory Factory => factory;
        public WorkLogService WorkLogs { get; } = new(factory, new ReportInvalidationService(factory));
        public ManualSourceService ManualSources { get; } = new(factory, new ReportInvalidationService(factory));
        public WorkDraftService Drafts => NewDraftService();
        public WorkDraftService NewDraftService() => new(factory, WorkLogs, ManualSources);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            db.Reports.Add(new ReportDocument { Kind = ReportKind.Daily, PeriodKey = "2026-09-07" });
            await db.SaveChangesAsync();
            return new Fixture(connection, factory);
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
