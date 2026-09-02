using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AiConfigurationService(IDbContextFactory<WorkLensDbContext> factory)
{
    private static readonly Guid ConfigurationId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public async Task<AiProviderConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var configuration = await db.AiProviders.SingleOrDefaultAsync(x => x.Id == ConfigurationId, cancellationToken);
        if (configuration is not null)
        {
            return configuration;
        }

        configuration = new AiProviderConfiguration { Id = ConfigurationId };
        db.AiProviders.Add(configuration);
        await db.SaveChangesAsync(cancellationToken);
        return configuration;
    }

    public async Task SaveAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken = default)
    {
        configuration.GeneralReportPrompt = configuration.GeneralReportPrompt?.Trim() ?? string.Empty;
        configuration.DailyReportPromptOverride = configuration.DailyReportPromptOverride?.Trim() ?? string.Empty;
        configuration.WeeklyReportPromptOverride = configuration.WeeklyReportPromptOverride?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuration.GeneralReportPrompt))
        {
            throw new ArgumentException("通用 AI 整理指令不可空白。", nameof(configuration));
        }
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AiProviders.SingleOrDefaultAsync(x => x.Id == ConfigurationId, cancellationToken);
        if (existing is null)
        {
            db.AiProviders.Add(configuration);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(configuration);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
