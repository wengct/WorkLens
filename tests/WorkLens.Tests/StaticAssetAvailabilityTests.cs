using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WorkLens.Tests;

public sealed class StaticAssetAvailabilityTests : IClassFixture<WorkLensApplicationFactory>
{
    private readonly HttpClient client;

    public StaticAssetAvailabilityTests(WorkLensApplicationFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Blazor_framework_script_is_served_without_a_launch_profile()
    {
        using var response = await client.GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "text/javascript",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/lib/easymde/easymde.min.js")]
    [InlineData("/lib/easymde/easymde.min.css")]
    [InlineData("/js/easymde-interop.js")]
    public async Task Offline_markdown_editor_assets_are_served(string path)
    {
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/reports?kind=Daily&date=2026-09-03")]
    [InlineData("/ai")]
    [InlineData("/sources")]
    public async Task Long_running_operation_pages_render_successfully(string path)
    {
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Removed_history_route_is_not_available()
    {
        using var response = await client.GetAsync("/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class WorkLensApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "WorkLens.Tests",
        Guid.NewGuid().ToString("N"));

    public WorkLensApplicationFactory()
    {
        Directory.CreateDirectory(testRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting(
            "ConnectionStrings:WorkLens",
            $"Data Source={Path.Combine(testRoot, "worklens.db")}");
        builder.UseSetting(
            "WorkLens:BackupPath",
            Path.Combine(testRoot, "backups"));
        builder.UseSetting(
            "WorkLens:LogPath",
            Path.Combine(testRoot, "logs"));
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WorkLens"] =
                    $"Data Source={Path.Combine(testRoot, "worklens.db")}",
                ["WorkLens:BackupPath"] = Path.Combine(testRoot, "backups"),
                ["WorkLens:LogPath"] = Path.Combine(testRoot, "logs")
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup is best effort; the unique temporary directory
                // is outside the repository and contains no user data.
            }
        }
    }
}
