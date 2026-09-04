using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task Initialize_adds_provider_type_and_headless_setting_to_an_existing_database()
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
        Assert.Equal("ask-bridge", configuration.ProviderType);
        Assert.True(configuration.UseHeadless);
        Assert.Null(configuration.ProtectedApiKey);
        Assert.Null(configuration.ApiEndpoint);
        Assert.Null(configuration.Model);
        Assert.Null(configuration.ApiVersion);
        Assert.Equal(AiReasoningLevel.Default, configuration.ReasoningLevel);
        Assert.Equal("預設 AI 設定", configuration.Name);
        Assert.True(configuration.IsDefault);
        Assert.Equal("整理", configuration.GeneralReportPrompt);
        Assert.True((await db.AiFeatureSettings.SingleAsync()).Enabled);
        Assert.Equal("整理", (await db.PromptTemplates.SingleAsync()).Content);
        Assert.Equal(4, await db.ScheduleDefinitions.CountAsync());

        var service = new AiConfigurationService(new Factory(options), new StubSecretProtector());
        var added = await service.SaveAsync(new AiProviderConfiguration
        {
            Name = "第二組",
            ProviderType = "ask-bridge"
        });
        Assert.Equal("第二組", added.Name);

        await new DatabaseInitializer(new Factory(options)).InitializeAsync();
        Assert.Equal(2, await db.AiProviders.AsNoTracking().CountAsync());
        Assert.Single(await db.AiFeatureSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Initialize_migrates_legacy_prompt_overrides_to_report_schedules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiProviders.Add(new AiProviderConfiguration
            {
                GeneralReportPrompt = "General",
                DailyReportPromptOverride = "Daily custom",
                WeeklyReportPromptOverride = "Weekly custom"
            });
            await setup.SaveChangesAsync();
        }

        await new DatabaseInitializer(new Factory(options)).InitializeAsync();

        await using var verify = new WorkLensDbContext(options);
        var dailySchedule = await verify.ScheduleDefinitions.SingleAsync(x => x.Kind == ScheduleKind.DailyReport);
        var weeklySchedule = await verify.ScheduleDefinitions.SingleAsync(x => x.Kind == ScheduleKind.WeeklyReport);
        Assert.Equal("Daily custom", (await verify.PromptTemplates.SingleAsync(x => x.Id == dailySchedule.PromptTemplateId)).Content);
        Assert.Equal("Weekly custom", (await verify.PromptTemplates.SingleAsync(x => x.Id == weeklySchedule.PromptTemplateId)).Content);
        Assert.Equal("General", (await verify.PromptTemplates.SingleAsync(x => x.IsDefault)).Content);
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
                    Name = "舊預設",
                    IsDefault = true,
                    GeneralReportPrompt = "請將資料整理成清楚、可直接交付的工作回報。保留具體成果、處理過程與下一步；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。"
                },
                new AiProviderConfiguration
                {
                    Id = Guid.NewGuid(),
                    Name = "自訂",
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

    private sealed class StubSecretProtector : IAiSecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
}
