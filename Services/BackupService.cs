using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class BackupService(
    IDbContextFactory<WorkLensDbContext> factory,
    AppPaths paths,
    RuntimeSettingsService runtimeSettings,
    ILogger<BackupService> logger)
{
    public async Task<BackupRecord?> CreateAsync(
        string kind,
        string periodKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(periodKey))
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.BackupRecords
            .SingleOrDefaultAsync(x => x.Kind == kind && x.PeriodKey == periodKey, cancellationToken);
        if (existing is not null)
        {
            if (await IsChecksumValidAsync(existing, cancellationToken))
            {
                return existing;
            }

            existing.IntegrityChecked = false;
            existing.Error = "既有備份檔案不存在或 checksum 不符，拒絕覆寫。";
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var backupPath = runtimeSettings.GetBackupPath();
        Directory.CreateDirectory(backupPath);
        var safeKind = string.Concat(kind.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        var safePeriod = string.Concat(periodKey.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        var databaseBackup = Path.Combine(backupPath, $"worklens-{safeKind}-{safePeriod}.db");
        var manifestPath = databaseBackup + ".manifest.json";

        try
        {
            await using var source = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await using var destination = new SqliteConnection($"Data Source={databaseBackup}");
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);

            var sha256 = await ComputeSha256Async(databaseBackup, cancellationToken);
            var manifest = new
            {
                schemaVersion = 1,
                kind,
                periodKey,
                createdAt = DateTimeOffset.UtcNow,
                database = Path.GetFileName(databaseBackup),
                sha256
            };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            var record = new BackupRecord
            {
                Kind = kind,
                PeriodKey = periodKey,
                FilePath = databaseBackup,
                Sha256 = sha256,
                IntegrityChecked = true
            };
            db.BackupRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            await PruneAsync(kind, cancellationToken);
            return record;
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            logger.LogWarning(exception, "建立 {Kind} 備份失敗", kind);
            return null;
        }
    }

    public async Task<bool> VerifyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.BackupRecords.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (record is null)
        {
            return false;
        }

        var valid = await IsChecksumValidAsync(record, cancellationToken);
        record.IntegrityChecked = valid;
        record.Error = valid ? null : "備份檔案不存在或 checksum 不符。";
        await db.SaveChangesAsync(cancellationToken);
        return valid;
    }

    private async Task PruneAsync(string kind, CancellationToken cancellationToken)
    {
        var keep = kind.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? 12 : 30;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.BackupRecords
            .Where(x => x.Kind == kind)
            .ToListAsync(cancellationToken);
        var oldRecords = records
            .OrderByDescending(x => x.CreatedAt)
            .Skip(keep)
            .ToList();
        foreach (var record in oldRecords)
        {
            TryDelete(record.FilePath);
            TryDelete(record.FilePath + ".manifest.json");
            db.BackupRecords.Remove(record);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<bool> IsChecksumValidAsync(
        BackupRecord record,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(record.FilePath))
        {
            return false;
        }

        try
        {
            var actual = await ComputeSha256Async(record.FilePath, cancellationToken);
            return actual.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Pruning is best effort and must not break the next scheduled run.
        }
    }
}
