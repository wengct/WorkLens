using Microsoft.EntityFrameworkCore;
using System.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Data;

public sealed class DatabaseInitializer(IDbContextFactory<WorkLensDbContext> factory)
{
    private const string PreviousGeneralReportPrompt = "請將資料整理成清楚、可直接交付的工作回報。保留具體成果、處理過程與下一步；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。";
    private const string PreviousCategorizedGeneralReportPrompt = "請將資料整理成清楚、可直接交付的工作回報，並依據工作內容自動歸類，按以下固定分類拆分章節：專案管理、UIUX相關、需求評估、功能開發、功能測試、BUG處理、文件相關、客服、其他。分類名稱、文字與順序不可更動；只建立有內容的章節。同一筆工作若涉及多個分類，歸入最主要的分類，避免重複。保留具體成果與處理過程；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。";
    private const string PreviousStructuredGeneralReportPrompt = """
        請將資料整理成清楚、可直接交付的工作回報，並依據工作內容自動歸類，按以下固定分類拆分章節：專案管理、UIUX相關、需求評估、功能開發、功能測試、BUG處理、文件相關、客服、其他。分類名稱、文字與順序不可更動；只建立有內容的章節。同一筆工作若涉及多個分類，歸入最主要的分類，避免重複。保留具體成果與處理過程；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。

        格式如下：

        # 每日工作回報

        ## 日期：{{YYYY-MM-DD}}

        ## 專案：{專案}

        ## 工作項目

        ### 專案管理

        - ...

        ### UIUX相關

        - ...
        """;
    private const string PreviousMultiProjectGeneralReportPrompt = """
        請將資料整理成清楚、可直接交付的工作回報，並依據工作內容自動歸類，按以下固定分類拆分章節：專案管理、UIUX相關、需求評估、功能開發、功能測試、BUG處理、文件相關、客服、其他。分類名稱、文字與順序不可更動；只建立有內容的章節。同一筆工作若涉及多個分類，歸入最主要的分類，避免重複。保留具體成果與處理過程；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。

        格式如下：

        # 每日工作回報

        ## 日期：{{YYYY-MM-DD}}

        ## 專案：{{專案1}}

        ## 工作項目：{{專案1}}

        ### 專案管理

        - ...

        ### UIUX相關

        - ...

        ## 專案：{{專案2}}

        ## 工作項目

        ### 專案管理

        - ...

        ### UIUX相關

        - ...
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureReportRevisionSchemaAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "WorkDrafts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_WorkDrafts" PRIMARY KEY,
                "Kind" TEXT NOT NULL,
                "Date" TEXT NOT NULL,
                "TargetId" TEXT NULL,
                "Payload" TEXT NOT NULL,
                "BaseVersion" TEXT NULL,
                "Version" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);
        await EnsureSchedulingSchemaAsync(db, cancellationToken);
        var legacyAiEnabled = await ReadLegacyAiEnabledAsync(db, cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "ProviderType", "TEXT NOT NULL DEFAULT 'ask-bridge'", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "ProtectedApiKey", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "ApiEndpoint", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "Model", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "ApiVersion", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "ReasoningLevel", "TEXT NOT NULL DEFAULT 'Default'", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "Name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "IsDefault", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "CreatedAt", "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00+00:00'", cancellationToken);
        await EnsureColumnAsync(db, "AiProviders", "UpdatedAt", "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00+00:00'", cancellationToken);
        await EnsureAiFeatureSettingsSchemaAsync(db, cancellationToken);
        await EnsureAiFeatureSettingsAsync(db, legacyAiEnabled, cancellationToken);
        await EnsureSensitiveWordsSchemaAsync(db, cancellationToken);
        await EnsureSensitiveScanExclusionsSchemaAsync(db, cancellationToken);
        await RemoveLegacyAiProviderEnabledColumnAsync(db, cancellationToken);
        await EnsureUseHeadlessColumnAsync(db, cancellationToken);
        await EnsureWorkEntryTitleColumnAsync(db, cancellationToken);
        await RecoverInterruptedSourceCollectionsAsync(db, cancellationToken);
        await RecoverInterruptedScheduleExecutionsAsync(db, cancellationToken);
        if (!await db.AiProviders.AnyAsync(cancellationToken))
        {
            db.AiProviders.Add(new AiProviderConfiguration
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Name = "預設 AI 設定",
                IsDefault = true
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await NormalizeAiProviderConfigurationsAsync(db, cancellationToken);
        await EnsureAiProviderIndexesAsync(db, cancellationToken);

        await UpgradeDefaultPromptAsync(db, cancellationToken);
        await SeedPromptTemplatesAndSchedulesAsync(db, cancellationToken);
        await MergeLegacyBackupSchedulesAsync(db, cancellationToken);
    }

    private static async Task<bool> ReadLegacyAiEnabledAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(db, "AiProviders", cancellationToken))
        {
            return false;
        }

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var columnCheck = connection.CreateCommand();
            columnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('AiProviders') WHERE name = 'Enabled';";
            if (Convert.ToInt32(await columnCheck.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return false;
            }

            await using var valueQuery = connection.CreateCommand();
            valueQuery.CommandText = """
                SELECT "Enabled"
                FROM "AiProviders"
                ORDER BY CASE WHEN "Id" = '00000000-0000-0000-0000-000000000001' THEN 0 ELSE 1 END, "Id"
                LIMIT 1;
                """;
            var value = await valueQuery.ExecuteScalarAsync(cancellationToken);
            return value is not null && value != DBNull.Value && Convert.ToInt32(value) != 0;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task EnsureAiFeatureSettingsSchemaAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "AiFeatureSettings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AiFeatureSettings" PRIMARY KEY,
                "Enabled" INTEGER NOT NULL
            );
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureAiFeatureSettingsAsync(
        WorkLensDbContext db,
        bool legacyEnabled,
        CancellationToken cancellationToken)
    {
        if (await db.AiFeatureSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        db.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = legacyEnabled });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RemoveLegacyAiProviderEnabledColumnAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('AiProviders') WHERE name = 'Enabled';";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE \"AiProviders\" DROP COLUMN \"Enabled\";";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task NormalizeAiProviderConfigurationsAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var configurations = await db.AiProviders.OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var legacyId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var selectedDefault = configurations
            .Where(x => x.IsDefault)
            .OrderBy(x => x.Id == legacyId ? 0 : 1)
            .FirstOrDefault()
            ?? configurations.OrderBy(x => x.Id == legacyId ? 0 : 1).First();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var configuration in configurations)
        {
            configuration.IsDefault = configuration.Id == selectedDefault.Id;
            if (configuration.CreatedAt == default) configuration.CreatedAt = now;
            if (configuration.UpdatedAt == default) configuration.UpdatedAt = configuration.CreatedAt;

            var baseName = string.IsNullOrWhiteSpace(configuration.Name)
                ? configuration.Id == selectedDefault.Id
                    ? "預設 AI 設定"
                    : ProviderDisplayName(configuration.ProviderType)
                : configuration.Name.Trim();
            var uniqueName = baseName;
            var suffix = 2;
            while (!usedNames.Add(uniqueName))
            {
                uniqueName = $"{baseName} ({suffix++})";
            }
            configuration.Name = uniqueName;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureAiProviderIndexesAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiProviders_Name" ON "AiProviders" ("Name" COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiProviders_IsDefault" ON "AiProviders" ("IsDefault") WHERE "IsDefault" = 1;
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static string ProviderDisplayName(string providerType) => providerType switch
    {
        "ask-bridge" => "ask-bridge",
        "openai" => "OpenAI",
        "azure-openai" => "Azure OpenAI",
        "anthropic" => "Anthropic",
        "gemini" => "Google Gemini",
        "openai-compatible" => "OpenAI Compatible",
        _ => "AI 設定"
    };

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
        await EnsureColumnAsync(db, "AiJobs", "SanitizationStatus", "TEXT NOT NULL DEFAULT 'NotRun'", cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "SanitizedFindingCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "SanitizedCategoriesJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "SanitizerVersion", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "AiJobs", "SanitizerRuleVersion", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ScheduleExecutions", "SanitizationStatus", "TEXT NOT NULL DEFAULT 'NotRun'", cancellationToken);
        await EnsureColumnAsync(db, "ScheduleExecutions", "SanitizedFindingCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "ScheduleExecutions", "SanitizedCategoriesJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(db, "ScheduleExecutions", "SanitizerVersion", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ScheduleExecutions", "SanitizerRuleVersion", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    private static async Task EnsureReportRevisionSchemaAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ReportRevisions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ReportRevisions" PRIMARY KEY,
                "ReportId" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "DeterministicBody" TEXT NOT NULL,
                "TotalHours" REAL NOT NULL,
                "IsStale" INTEGER NOT NULL,
                "GeneratedAt" TEXT NULL,
                "AiJobId" TEXT NULL,
                "SourceVersion" INTEGER NOT NULL,
                "Reason" TEXT NOT NULL,
                "CapturedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReportRevisions_ReportId" ON "ReportRevisions" ("ReportId");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        await EnsureColumnAsync(db, "Reports", "UpdateVersion", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
    }

    private static async Task EnsureSensitiveWordsSchemaAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "SensitiveWords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SensitiveWords" PRIMARY KEY,
                "Value" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SensitiveWords_Value" ON "SensitiveWords" ("Value" COLLATE NOCASE);
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureSensitiveScanExclusionsSchemaAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "SensitiveScanExclusions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SensitiveScanExclusions" PRIMARY KEY,
                "Value" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SensitiveScanExclusions_Value" ON "SensitiveScanExclusions" ("Value" COLLATE NOCASE);
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
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

    private static async Task MergeLegacyBackupSchedulesAsync(
        WorkLensDbContext db,
        CancellationToken cancellationToken)
    {
        var backup = await db.ScheduleDefinitions
            .SingleOrDefaultAsync(x => x.Kind == ScheduleKind.DailyBackup, cancellationToken);
        var legacyWeekly = await db.ScheduleDefinitions
            .SingleOrDefaultAsync(x => x.Kind == ScheduleKind.WeeklyBackup, cancellationToken);
        if (backup is null || legacyWeekly is null || !legacyWeekly.Enabled)
        {
            return;
        }

        if (!backup.Enabled)
        {
            backup.Enabled = true;
            backup.DaysOfWeekMask = legacyWeekly.DaysOfWeekMask;
            backup.Hour = legacyWeekly.Hour;
            backup.Minute = legacyWeekly.Minute;
        }
        else
        {
            backup.DaysOfWeekMask |= legacyWeekly.DaysOfWeekMask;
        }

        backup.UpdatedAt = DateTimeOffset.UtcNow;
        legacyWeekly.Enabled = false;
        legacyWeekly.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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
            .Where(configuration =>
                configuration.GeneralReportPrompt == PreviousGeneralReportPrompt ||
                configuration.GeneralReportPrompt == PreviousCategorizedGeneralReportPrompt ||
                configuration.GeneralReportPrompt == PreviousStructuredGeneralReportPrompt ||
                configuration.GeneralReportPrompt == PreviousMultiProjectGeneralReportPrompt)
            .ToListAsync(cancellationToken);
        var templates = await db.PromptTemplates
            .Where(template =>
                template.IsDefault &&
                (template.Content == PreviousGeneralReportPrompt ||
                 template.Content == PreviousCategorizedGeneralReportPrompt ||
                 template.Content == PreviousStructuredGeneralReportPrompt ||
                 template.Content == PreviousMultiProjectGeneralReportPrompt))
            .ToListAsync(cancellationToken);

        foreach (var configuration in configurations)
        {
            configuration.GeneralReportPrompt = AiPromptDefaults.GeneralReportPrompt;
        }

        foreach (var template in templates)
        {
            template.Content = AiPromptDefaults.GeneralReportPrompt;
            template.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (configurations.Count > 0 || templates.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
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
