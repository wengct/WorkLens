using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class PromptTemplateServiceTests
{
    [Fact]
    public async Task Default_can_be_changed_but_not_archived()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var service = new PromptTemplateService(new Factory(options));
        var first = await service.SaveAsync(new PromptTemplate { Name = "A", Content = "Prompt A" });
        var second = await service.SaveAsync(new PromptTemplate { Name = "B", Content = "Prompt B" });

        await service.SetDefaultAsync(first.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArchivedAsync(first.Id, true));
        await service.SetDefaultAsync(second.Id);
        await service.SetArchivedAsync(first.Id, true);

        var templates = await service.GetAllAsync();
        Assert.True(templates.Single(x => x.Id == second.Id).IsDefault);
        Assert.True(templates.Single(x => x.Id == first.Id).IsArchived);
    }

    [Fact]
    public async Task Effective_prompt_uses_selected_template_then_default()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var service = new PromptTemplateService(new Factory(options));
        var fallback = await service.SaveAsync(new PromptTemplate { Name = "Default", Content = "Default text" });
        var selected = await service.SaveAsync(new PromptTemplate { Name = "Selected", Content = "Selected text" });
        await service.SetDefaultAsync(fallback.Id);

        Assert.Equal(selected.Id, (await service.GetEffectiveAsync(selected.Id)).Id);
        Assert.Equal(fallback.Id, (await service.GetEffectiveAsync(null)).Id);
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
