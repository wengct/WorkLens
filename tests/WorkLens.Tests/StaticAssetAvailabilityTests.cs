using System.Net;
using System.Net.Http.Json;
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

    [Fact]
    public async Task Health_endpoint_reports_ready_and_the_assembly_version()
    {
        using var response = await client.GetAsync("/healthz");
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("Healthy", payload.Status);
        Assert.False(string.IsNullOrWhiteSpace(payload.Version));
    }

    [Theory]
    [InlineData("/lib/easymde/easymde.min.js")]
    [InlineData("/lib/easymde/easymde.min.css")]
    [InlineData("/js/easymde-interop.js")]
    [InlineData("/js/action-menus.js")]
    public async Task Offline_frontend_assets_are_served(string path)
    {
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
    }

    [Fact]
    public async Task Windows_development_build_copies_the_bundled_scanner_layout()
    {
        var project = await File.ReadAllTextAsync(FindRepositoryFile("WorkLens.csproj"));

        Assert.Contains("tools\\leak-hunter\\leak-hunter.exe", project, StringComparison.Ordinal);
        Assert.Contains("scripts\\leak-hunter.version", project, StringComparison.Ordinal);
        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
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

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}

public sealed record HealthPayload(string Status, string Version);

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
