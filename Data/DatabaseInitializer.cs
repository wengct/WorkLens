using Microsoft.EntityFrameworkCore;
using System.Data;
using WorkLens.Domain;

namespace WorkLens.Data;

public sealed class DatabaseInitializer(IDbContextFactory<WorkLensDbContext> factory)
{
    private const string PreviousGeneralReportPrompt = "請將資料整理成清楚、可直接交付的工作回報。保留具體成果、處理過程與下一步；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureUseHeadlessColumnAsync(db, cancellationToken);
        await EnsureWorkEntryTitleColumnAsync(db, cancellationToken);
        await RecoverInterruptedSourceCollectionsAsync(db, cancellationToken);
        if (!await db.AiProviders.AnyAsync(cancellationToken))
        {
            db.AiProviders.Add(new AiProviderConfiguration());
            await db.SaveChangesAsync(cancellationToken);
        }

        await UpgradeDefaultPromptAsync(db, cancellationToken);
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
