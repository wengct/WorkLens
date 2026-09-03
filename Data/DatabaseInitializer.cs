using Microsoft.EntityFrameworkCore;
using System.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Data;

public sealed class DatabaseInitializer(IDbContextFactory<WorkLensDbContext> factory)
{
    private const string PreviousGeneralReportPrompt = "請將資料整理成清楚、可直接交付的工作回報。保留具體成果、處理過程與下一步；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSchedulingSchemaAsync(db, cancellationToken);
        await EnsureUseHeadlessColumnAsync(db, cancellationToken);
        await EnsureWorkEntryTitleColumnAsync(db, cancellationToken);
        await RecoverInterruptedSourceCollectionsAsync(db, cancellationToken);
        await RecoverInterruptedScheduleExecutionsAsync(db, cancellationToken);
        if (!await db.AiProviders.AnyAsync(cancellationToken))
        {
            db.AiProviders.Add(new AiProviderConfiguration());
            await db.SaveChangesAsync(cancellationToken);
        }

        await UpgradeDefaultPromptAsync(db, cancellationToken);
        await SeedPromptTemplatesAndSchedulesAsync(db, cancellationToken);
    }

    private static async Task EnsureSchedulingSchemaAsync(WorkLensDbContext db, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "PromptTemplates" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PromptTemplates" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "IsDefault" INTEGER NOT NULL,
                "IsArchived" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "ScheduleDefinitions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ScheduleDefinitions" PRIMARY KEY,
                "Kind" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "DaysOfWeekMask" INTEGER NOT NULL,
                "Hour" INTEGER NOT NULL,
                "Minute" INTEGER NOT NULL,
                "PromptTemplateId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "ScheduleExecutions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ScheduleExecutions" PRIMARY KEY,
                "ScheduleId" TEXT NOT NULL,
                "PeriodKey" TEXT NOT NULL,
                "IsManual" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "DeterministicStatus" TEXT NOT NULL,
                "AiStatus" TEXT NOT NULL,
                "BackupStatus" TEXT NOT NULL,
                "PromptTemplateId" TEXT NULL,
                "PromptNameSnapshot" TEXT NOT NULL,
                "PromptTextSnapshot" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "Error" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_PromptTemplates_Name" ON "PromptTemplates" ("Name");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PromptTemplates_IsDefault" ON "PromptTemplates" ("IsDefault") WHERE "IsDefault" = 1;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ScheduleDefinitions_Kind" ON "ScheduleDefinitions" ("Kind");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ScheduleExecutions_ScheduleId_PeriodKey" ON "ScheduleExecutions" ("ScheduleId", "PeriodKey");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "PromptTemplateId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "PromptNameSnapshot", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "PromptTextSnapshot", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        WorkLensDbContext db,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var tableCheck = connection.CreateCommand();
            tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
            var tableParameter = tableCheck.CreateParameter();
            tableParameter.ParameterName = "$table";
            tableParameter.Value = table;
            tableCheck.Parameters.Add(tableParameter);
            if (Convert.ToInt32(await tableCheck.ExecuteScalarAsync(cancellationToken)) == 0) return;

            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
            var parameter = check.CreateParameter();
            parameter.ParameterName = "$column";
            parameter.Value = column;
            check.Parameters.Add(parameter);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {declaration};";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task SeedPromptTemplatesAndSchedulesAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        PromptTemplate? general = await db.PromptTemplates.SingleOrDefaultAsync(x => x.IsDefault, cancellationToken);
        PromptTemplate? daily = null;
        PromptTemplate? weekly = null;
        if (general is null)
        {
            var configuration = await db.AiProviders.AsNoTracking()
                .OrderBy(x => x.Id == Guid.Parse("00000000-0000-0000-0000-000000000001") ? 0 : 1)
                .FirstAsync(cancellationToken);
            general = new PromptTemplate
            {
                Name = "預設工作回報",
                Content = string.IsNullOrWhiteSpace(configuration.GeneralReportPrompt)
                    ? AiPromptDefaults.GeneralReportPrompt
                    : configuration.GeneralReportPrompt.Trim(),
                IsDefault = true
            };
            db.PromptTemplates.Add(general);
            if (!string.IsNullOrWhiteSpace(configuration.DailyReportPromptOverride))
            {
                daily = new PromptTemplate { Name = "舊版每日報告", Content = configuration.DailyReportPromptOverride.Trim() };
                db.PromptTemplates.Add(daily);
            }
            if (!string.IsNullOrWhiteSpace(configuration.WeeklyReportPromptOverride))
            {
                weekly = new PromptTemplate { Name = "舊版每週報告", Content = configuration.WeeklyReportPromptOverride.Trim() };
                db.PromptTemplates.Add(weekly);
            }
        }

        var existingKinds = await db.ScheduleDefinitions.Select(x => x.Kind).ToListAsync(cancellationToken);
        foreach (var schedule in ScheduleDefaults.Create().Where(x => !existingKinds.Contains(x.Kind)))
        {
            if (schedule.Kind == ScheduleKind.DailyReport) schedule.PromptTemplateId = daily?.Id;
            if (schedule.Kind == ScheduleKind.WeeklyReport) schedule.PromptTemplateId = weekly?.Id;
            db.ScheduleDefinitions.Add(schedule);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RecoverInterruptedScheduleExecutionsAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var interrupted = await db.ScheduleExecutions.Where(x => x.Status == "Running").ToListAsync(cancellationToken);
        foreach (var execution in interrupted)
        {
            execution.Status = "Interrupted";
            execution.Error = "WorkLens 在作業完成前停止。";
            execution.CompletedAt = DateTimeOffset.UtcNow;
        }
        if (interrupted.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RecoverInterruptedSourceCollectionsAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(db, "ActivitySources", cancellationToken))
        {
            return;
        }

        var interruptedSources = await db.ActivitySources
            .Where(source => source.HealthStatus == SourceHealthStatus.Running)
            .ToListAsync(cancellationToken);
        if (interruptedSources.Count == 0)
        {
            return;
        }

        foreach (var source in interruptedSources)
        {
            source.HealthStatus = source.Enabled ? SourceHealthStatus.Ready : SourceHealthStatus.Disabled;
            source.LastError = null;
            source.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        WorkLensDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$tableName";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task UpgradeDefaultPromptAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var configurations = await db.AiProviders
            .Where(configuration => configuration.GeneralReportPrompt == PreviousGeneralReportPrompt)
            .ToListAsync(cancellationToken);
        if (configurations.Count == 0)
        {
            return;
        }

        foreach (var configuration in configurations)
        {
            configuration.GeneralReportPrompt = AiPromptDefaults.GeneralReportPrompt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureUseHeadlessColumnAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('AiProviders') WHERE name = 'UseHeadless';";
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"AiProviders\" ADD COLUMN \"UseHeadless\" INTEGER NOT NULL DEFAULT 1;",
                    cancellationToken);
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task EnsureWorkEntryTitleColumnAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var tableCheck = connection.CreateCommand();
            tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'WorkEntries';";
            var tableExists = Convert.ToInt32(await tableCheck.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!tableExists)
            {
                return;
            }

            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkEntries') WHERE name = 'Title';";
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"WorkEntries\" ADD COLUMN \"Title\" TEXT NOT NULL DEFAULT '';",
                    cancellationToken);
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }

        var entries = await db.WorkEntries
            .Where(entry => entry.Title == string.Empty)
            .ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            entry.Title = ContentTitle.Resolve(null, entry.WorkContent);
        }

        if (entries.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
