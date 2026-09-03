using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task Initialize_adds_headless_setting_to_an_existing_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "AiProviders" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AiProviders" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL,
                    "Provider" TEXT NOT NULL,
                    "ExecutablePath" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "DetectedVersion" TEXT NULL,
                    "LastError" TEXT NULL,
                    "LastCheckedAt" TEXT NULL,
                    "GeneralReportPrompt" TEXT NOT NULL,
                    "DailyReportPromptOverride" TEXT NOT NULL,
                    "WeeklyReportPromptOverride" TEXT NOT NULL
                );
                INSERT INTO "AiProviders" (
                    "Id", "Enabled", "Provider", "Status", "GeneralReportPrompt",
                    "DailyReportPromptOverride", "WeeklyReportPromptOverride")
                VALUES (
                    '00000000-0000-0000-0000-000000000001', 1, 'chatgpt', 'Ready',
                    '整理', '', '');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite(connection)
            .Options;
        await new DatabaseInitializer(new Factory(options)).InitializeAsync();

        await using var db = new WorkLensDbContext(options);
        var configuration = await db.AiProviders.SingleAsync();
        Assert.True(configuration.UseHeadless);
        Assert.Equal("整理", configuration.GeneralReportPrompt);
    }

    [Fact]
    public async Task Initialize_upgrades_the_previous_default_prompt_without_overwriting_custom_prompts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiProviders.AddRange(
                new AiProviderConfiguration
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    GeneralReportPrompt = "請將資料整理成清楚、可直接交付的工作回報。保留具體成果、處理過程與下一步；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。"
                },
                new AiProviderConfiguration
                {
                    Id = Guid.NewGuid(),
                    GeneralReportPrompt = "我的自訂整理方式"
                });
            await setup.SaveChangesAsync();
        }

        await new DatabaseInitializer(new Factory(options)).InitializeAsync();

        await using var db = new WorkLensDbContext(options);
        var prompts = await db.AiProviders
            .OrderBy(configuration => configuration.Id)
            .Select(configuration => configuration.GeneralReportPrompt)
            .ToListAsync();
        Assert.Contains(AiPromptDefaults.GeneralReportPrompt, prompts);
        Assert.Contains("我的自訂整理方式", prompts);
    }

    [Fact]
    public async Task Initialize_adds_and_backfills_work_entry_titles_in_an_existing_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            await setup.Database.ExecuteSqlRawAsync("DROP TABLE \"WorkEntries\";");
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "WorkEntries" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_WorkEntries" PRIMARY KEY,
                    "WorkDate" TEXT NOT NULL,
                    "Hours" REAL NOT NULL,
                    "WorkContent" TEXT NOT NULL,
                    "ProjectId" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                INSERT INTO "WorkEntries" (
                    "Id", "WorkDate", "Hours", "WorkContent", "CreatedAt", "UpdatedAt")
                VALUES (
                    '11111111-1111-1111-1111-111111111111', '2026-09-03', 2,
                    '# 舊紀錄標題

                    內容', '2026-09-03 00:00:00+00:00', '2026-09-03 00:00:00+00:00');
                """);
        }

        await new DatabaseInitializer(new Factory(options)).InitializeAsync();

        await using var verify = new WorkLensDbContext(options);
        Assert.Equal("舊紀錄標題", (await verify.WorkEntries.SingleAsync()).Title);
    }

    [Fact]
    public async Task Initialize_recovers_source_collection_interrupted_by_a_previous_process()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ActivitySources.Add(new ActivitySource
            {
                DisplayName = "Interrupted source",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Running
            });
            await setup.SaveChangesAsync();
        }

        await new DatabaseInitializer(new Factory(options)).InitializeAsync();

        await using var verify = new WorkLensDbContext(options);
        Assert.Equal(SourceHealthStatus.Ready, (await verify.ActivitySources.SingleAsync()).HealthStatus);
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
