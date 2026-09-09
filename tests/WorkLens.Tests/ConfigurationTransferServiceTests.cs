using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ConfigurationTransferServiceTests
{
    [Fact]
    public async Task ExportAsync_ai_settings_omits_protected_api_key_and_unselected_categories()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.AiProviders.Add(new AiProviderConfiguration { Name = "OpenAI", ProviderType = "openai", ProtectedApiKey = "encrypted-secret", GeneralReportPrompt = "prompt" });
            db.SensitiveWords.Add(new SensitiveWord { Value = "private-word" });
            await db.SaveChangesAsync();
        }
        var json = await new ConfigurationTransferService(fixture.Factory).ExportAsync(new ConfigurationTransferSelection(AiSettings: true));
        Assert.Contains("OpenAI", json, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-word", json, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedApiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_imported_source_is_disabled_and_checkpoint_is_reset()
    {
        await using var fixture = await Fixture.CreateAsync();
        var document = new ConfigurationTransferDocument { Includes = new ConfigurationTransferSelection(Sources: true), Sources = [new TransferSource("Git", ActivitySourceType.WindowsGit, 10, 7, true, null, "{}")] };
        var result = await new ConfigurationTransferService(fixture.Factory).ApplyAsync(document, new Dictionary<string, TransferConflictAction>());
        Assert.True(result.Succeeded);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var source = await db.ActivitySources.SingleAsync();
        Assert.False(source.Enabled);
        Assert.Equal(SourceHealthStatus.Disabled, source.HealthStatus);
        Assert.Equal("{}", source.CheckpointJson);
    }

    [Fact]
    public async Task ApplyAsync_ai_settings_disables_feature_until_user_verifies_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            await db.SaveChangesAsync();
        }

        var document = new ConfigurationTransferDocument
        {
            Includes = new ConfigurationTransferSelection(AiSettings: true),
            AiEnabled = true,
            AiProviders = [new TransferAiProvider("Imported", true, "ask-bridge", true, "chatgpt", "C:\\tool\\ask-bridge", null, null, null, AiReasoningLevel.Default, "prompt", "", "")]
        };
        var result = await new ConfigurationTransferService(fixture.Factory).ApplyAsync(document, new Dictionary<string, TransferConflictAction>());

        Assert.True(result.Succeeded);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.False((await verify.AiFeatureSettings.SingleAsync()).Enabled);
        var provider = await verify.AiProviders.SingleAsync();
        Assert.Equal("NotConfigured", provider.Status);
        Assert.Null(provider.LastCheckedAt);
    }

    private sealed class Fixture(SqliteConnection connection, DbContextOptions<WorkLensDbContext> options) : IAsyncDisposable
    {
        public IDbContextFactory<WorkLensDbContext> Factory { get; } = new Factory(options);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
            await using var db = new WorkLensDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, options);
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
