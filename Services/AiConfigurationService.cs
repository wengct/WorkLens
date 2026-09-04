using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AiConfigurationService(
    IDbContextFactory<WorkLensDbContext> factory,
    IAiSecretProtector secrets)
{
    public async Task<IReadOnlyList<AiProviderConfiguration>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AiProviders.AsNoTracking()
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<AiProviderConfiguration?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AiProviders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<AiProviderConfiguration?> GetDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AiProviders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IsDefault, cancellationToken);
    }

    public async Task<AiFeatureSettings> GetFeatureSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AiFeatureSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == AiFeatureSettings.SingletonId, cancellationToken);
        return settings ?? new AiFeatureSettings();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AiFeatureSettings
            .SingleOrDefaultAsync(x => x.Id == AiFeatureSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            db.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = enabled });
        }
        else
        {
            settings.Enabled = enabled;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<AiProviderConfiguration> SaveAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SaveAsync(configuration, null, false, cancellationToken);

    public async Task<AiProviderConfiguration> SaveAsync(
        AiProviderConfiguration configuration,
        string? apiKey,
        bool clearApiKey,
        CancellationToken cancellationToken = default)
    {
        NormalizeAndValidate(configuration);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var duplicate = await db.AiProviders.AnyAsync(
            x => x.Id != configuration.Id &&
                 EF.Functions.Collate(x.Name, "NOCASE") == configuration.Name,
            cancellationToken);
        if (duplicate)
        {
            throw new ArgumentException("AI 設定名稱不可重複。", nameof(configuration));
        }

        var existing = await db.AiProviders.SingleOrDefaultAsync(x => x.Id == configuration.Id, cancellationToken);
        var protectedApiKey = existing?.ProtectedApiKey;
        if (clearApiKey || existing is not null &&
            !existing.ProviderType.Equals(configuration.ProviderType, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(apiKey))
        {
            protectedApiKey = null;
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            protectedApiKey = secrets.Protect(apiKey.Trim());
        }

        if (existing is null)
        {
            configuration.Id = configuration.Id == Guid.Empty ? Guid.NewGuid() : configuration.Id;
            configuration.IsDefault = false;
            configuration.ProtectedApiKey = protectedApiKey;
            configuration.CreatedAt = DateTimeOffset.UtcNow;
            configuration.UpdatedAt = configuration.CreatedAt;
            db.AiProviders.Add(configuration);
            existing = configuration;
        }
        else
        {
            CopyEditableValues(configuration, existing);
            existing.ProtectedApiKey = protectedApiKey;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var selected = await db.AiProviders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ArgumentException("找不到 AI 設定。", nameof(id));
        var currentDefaults = await db.AiProviders
            .Where(x => x.IsDefault && x.Id != id)
            .ToListAsync(cancellationToken);
        foreach (var configuration in currentDefaults)
        {
            configuration.IsDefault = false;
            configuration.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (currentDefaults.Count > 0) await db.SaveChangesAsync(cancellationToken);

        selected.IsDefault = true;
        selected.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var configuration = await db.AiProviders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ArgumentException("找不到 AI 設定。", nameof(id));
        if (configuration.IsDefault)
        {
            throw new InvalidOperationException("預設 AI 設定不可刪除；請先設定另一組預設設定。");
        }

        db.AiProviders.Remove(configuration);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void NormalizeAndValidate(AiProviderConfiguration configuration)
    {
        configuration.Name = configuration.Name?.Trim() ?? string.Empty;
        configuration.GeneralReportPrompt = configuration.GeneralReportPrompt?.Trim() ?? string.Empty;
        configuration.DailyReportPromptOverride = configuration.DailyReportPromptOverride?.Trim() ?? string.Empty;
        configuration.WeeklyReportPromptOverride = configuration.WeeklyReportPromptOverride?.Trim() ?? string.Empty;
        configuration.ApiEndpoint = NullIfWhiteSpace(configuration.ApiEndpoint);
        configuration.Model = NullIfWhiteSpace(configuration.Model);
        configuration.ApiVersion = NullIfWhiteSpace(configuration.ApiVersion);
        configuration.ExecutablePath = NullIfWhiteSpace(configuration.ExecutablePath);
        if (string.IsNullOrWhiteSpace(configuration.Name))
        {
            throw new ArgumentException("AI 設定名稱不可空白。", nameof(configuration));
        }
        if (string.IsNullOrWhiteSpace(configuration.GeneralReportPrompt))
        {
            throw new ArgumentException("通用 AI 整理指令不可空白。", nameof(configuration));
        }
    }

    private static void CopyEditableValues(
        AiProviderConfiguration source,
        AiProviderConfiguration destination)
    {
        destination.Name = source.Name;
        destination.ProviderType = source.ProviderType;
        destination.UseHeadless = source.UseHeadless;
        destination.Provider = source.Provider;
        destination.ExecutablePath = source.ExecutablePath;
        destination.ApiEndpoint = source.ApiEndpoint;
        destination.Model = source.Model;
        destination.ApiVersion = source.ApiVersion;
        destination.ReasoningLevel = source.ReasoningLevel;
        destination.Status = source.Status;
        destination.DetectedVersion = source.DetectedVersion;
        destination.LastError = source.LastError;
        destination.LastCheckedAt = source.LastCheckedAt;
        destination.GeneralReportPrompt = source.GeneralReportPrompt;
        destination.DailyReportPromptOverride = source.DailyReportPromptOverride;
        destination.WeeklyReportPromptOverride = source.WeeklyReportPromptOverride;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
