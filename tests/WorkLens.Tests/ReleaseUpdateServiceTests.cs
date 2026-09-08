using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ReleaseUpdateServiceTests
{
    [Theory]
    [InlineData("1.2.3", "v1.2.4", true)]
    [InlineData("1.9.0", "v1.10.0", true)]
    [InlineData("1.2.3", "v1.2.3", false)]
    [InlineData("1.2.4", "v1.2.3", false)]
    [InlineData("1.2.3-preview.1", "v1.2.3", true)]
    public void Version_comparison_follows_semantic_version_order(
        string currentValue, string latestValue, bool expected)
    {
        Assert.True(ReleaseVersion.TryParse(currentValue, out var current));
        Assert.True(ReleaseVersion.TryParse(latestValue, out var latest));
        Assert.Equal(expected, latest.CompareTo(current) > 0);
    }

    [Fact]
    public async Task Newer_release_is_reported_with_its_release_page()
    {
        var service = new ReleaseUpdateService(
            new StubHttpClientFactory("""{"tag_name":"v99.0.0"}"""),
            new StartupHealth(),
            NullLogger<ReleaseUpdateService>.Instance);

        var result = await service.CheckAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("v99.0.0", result.LatestVersion);
        Assert.Equal("https://github.com/wengct/WorkLens/releases/tag/v99.0.0", result.ReleaseUrl);
    }

    [Fact]
    public async Task Network_failure_does_not_escape_the_update_check()
    {
        var service = new ReleaseUpdateService(
            new ThrowingHttpClientFactory(), new StartupHealth(), NullLogger<ReleaseUpdateService>.Instance);

        var result = await service.CheckAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task Invalid_GitHub_response_does_not_escape_the_update_check()
    {
        var service = new ReleaseUpdateService(
            new StubHttpClientFactory("not-json"),
            new StartupHealth(),
            NullLogger<ReleaseUpdateService>.Instance);

        var result = await service.CheckAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    private sealed class StubHttpClientFactory(string response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
