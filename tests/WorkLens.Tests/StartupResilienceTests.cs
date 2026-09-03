using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WorkLens.Tests;

public sealed class StartupResilienceTests : IClassFixture<BrokenDatabaseApplicationFactory>
{
    private readonly HttpClient client;

    public StartupResilienceTests(BrokenDatabaseApplicationFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Database_initialization_failure_does_not_stop_the_host()
    {
        using var response = await client.GetAsync("/error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Database_initialization_failure_reports_unhealthy_without_internal_details()
    {
        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"Unhealthy\"", body, StringComparison.Ordinal);
        Assert.Contains("\"version\":", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-database", body, StringComparison.Ordinal);
        Assert.DoesNotContain("exception", body, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BrokenDatabaseApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "WorkLens.Tests",
        Guid.NewGuid().ToString("N"));

    public BrokenDatabaseApplicationFactory()
    {
        Directory.CreateDirectory(Path.Combine(testRoot, "not-a-database"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting(
            "ConnectionStrings:WorkLens",
            $"Data Source={Path.Combine(testRoot, "not-a-database")}");
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
                    $"Data Source={Path.Combine(testRoot, "not-a-database")}",
                ["WorkLens:BackupPath"] = Path.Combine(testRoot, "backups"),
                ["WorkLens:LogPath"] = Path.Combine(testRoot, "logs")
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(testRoot))
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
