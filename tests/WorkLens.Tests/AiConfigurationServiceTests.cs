using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AiConfigurationServiceTests
{
    [Fact]
    public async Task Save_creates_named_configuration_and_rejects_case_insensitive_duplicate()
    {
        await using var fixture = await Fixture.CreateAsync();

        var saved = await fixture.Service.SaveAsync(
            new AiProviderConfiguration
            {
                Name = " Secondary ",
                ProviderType = "openai",
                Model = "gpt-5.4"
            },
            "secret-two",
            false);

        Assert.Equal("Secondary", saved.Name);
        Assert.Equal("protected:secret-two", saved.ProtectedApiKey);
        Assert.False(saved.IsDefault);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SaveAsync(
            new AiProviderConfiguration { Name = "secondary" }));
        Assert.Contains("不可重複", exception.Message);
    }

    [Fact]
    public async Task Save_keeps_api_keys_isolated_and_clears_only_changed_provider()
    {
        await using var fixture = await Fixture.CreateAsync();
        var second = await fixture.Service.SaveAsync(
            new AiProviderConfiguration { Name = "第二組", ProviderType = "openai", Model = "gpt-5.4" },
            "second-key",
            false);
        var first = await fixture.Service.GetDefaultAsync();
        Assert.NotNull(first);
        await fixture.Service.SaveAsync(first!, "first-key", false);

        second.ProviderType = "anthropic";
        await fixture.Service.SaveAsync(second);

        await using var verify = fixture.CreateDbContext();
        var configurations = await verify.AiProviders.AsNoTracking().ToListAsync();
        Assert.Equal("protected:first-key", configurations.Single(x => x.IsDefault).ProtectedApiKey);
        Assert.Null(configurations.Single(x => x.Id == second.Id).ProtectedApiKey);
    }

    [Fact]
    public async Task SetDefault_is_atomic_and_only_non_default_configuration_can_be_deleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Service.GetDefaultAsync();
        var second = await fixture.Service.SaveAsync(new AiProviderConfiguration { Name = "第二組" });

        await fixture.Service.SetDefaultAsync(second.Id);

        await using (var verify = fixture.CreateDbContext())
        {
            var defaults = await verify.AiProviders.AsNoTracking().Where(x => x.IsDefault).ToListAsync();
            Assert.Single(defaults);
            Assert.Equal(second.Id, defaults[0].Id);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteAsync(second.Id));
        await fixture.Service.DeleteAsync(original!.Id);
        Assert.Single(await fixture.Service.GetAllAsync());
    }

    [Fact]
    public async Task Global_enabled_setting_survives_default_switch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var second = await fixture.Service.SaveAsync(new AiProviderConfiguration { Name = "第二組" });

        await fixture.Service.SetEnabledAsync(true);
        await fixture.Service.SetDefaultAsync(second.Id);

        Assert.True((await fixture.Service.GetFeatureSettingsAsync()).Enabled);
        Assert.Equal(second.Id, (await fixture.Service.GetDefaultAsync())!.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<WorkLensDbContext> options;

        private Fixture(SqliteConnection connection, DbContextOptions<WorkLensDbContext> options)
        {
            this.connection = connection;
            this.options = options;
            Service = new AiConfigurationService(new Factory(options), new StubSecretProtector());
        }

        public AiConfigurationService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
            await using var db = new WorkLensDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.AiProviders.Add(new AiProviderConfiguration
            {
                Name = "主要設定",
                IsDefault = true,
                ProtectedApiKey = "protected:original"
            });
            db.AiFeatureSettings.Add(new AiFeatureSettings());
            await db.SaveChangesAsync();
            return new Fixture(connection, options);
        }

        public WorkLensDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }

    private sealed class StubSecretProtector : IAiSecretProtector
    {
        public string Protect(string value) => $"protected:{value}";
        public string Unprotect(string value) => value["protected:".Length..];
    }
}
