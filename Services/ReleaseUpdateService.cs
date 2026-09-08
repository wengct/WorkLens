using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Services;

public sealed class ReleaseUpdateService(
    IHttpClientFactory httpClientFactory,
    StartupHealth startupHealth,
    ILogger<ReleaseUpdateService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim checkLock = new(1, 1);
    private ReleaseUpdateStatus? cachedStatus;
    private DateTimeOffset cacheExpiresAt;

    public async Task<ReleaseUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (cachedStatus is not null && DateTimeOffset.UtcNow < cacheExpiresAt) return cachedStatus;

        await checkLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedStatus is not null && DateTimeOffset.UtcNow < cacheExpiresAt) return cachedStatus;

            var currentVersion = startupHealth.Version;
            try
            {
                var client = httpClientFactory.CreateClient("GitHubReleases");
                var release = await client.GetFromJsonAsync<GitHubRelease>(
                    "repos/wengct/WorkLens/releases/latest", cancellationToken);
                if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                {
                    return Cache(ReleaseUpdateStatus.Unavailable(currentVersion));
                }

                var updateAvailable = ReleaseVersion.TryParse(currentVersion, out var current)
                    && ReleaseVersion.TryParse(release.TagName, out var latest)
                    && latest.CompareTo(current) > 0;

                return Cache(new ReleaseUpdateStatus(
                    currentVersion,
                    release.TagName.Trim(),
                    updateAvailable,
                    $"https://github.com/wengct/WorkLens/releases/tag/{Uri.EscapeDataString(release.TagName.Trim())}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                HttpRequestException or
                JsonException or
                NotSupportedException or
                OperationCanceledException)
            {
                logger.LogInformation(exception, "暫時無法檢查 GitHub 上的 WorkLens 最新版本。");
                return Cache(ReleaseUpdateStatus.Unavailable(currentVersion));
            }
        }
        finally
        {
            checkLock.Release();
        }
    }

    private ReleaseUpdateStatus Cache(ReleaseUpdateStatus status)
    {
        cachedStatus = status;
        cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
        return status;
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string TagName);
}

public sealed record ReleaseUpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    string? ReleaseUrl)
{
    public static ReleaseUpdateStatus Unavailable(string currentVersion) => new(currentVersion, null, false, null);
}

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, bool IsPrerelease)
    : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim().TrimStart('v', 'V');
        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0) normalized = normalized[..metadataIndex];
        var prereleaseIndex = normalized.IndexOf('-');
        var isPrerelease = prereleaseIndex >= 0;
        if (isPrerelease) normalized = normalized[..prereleaseIndex];

        var parts = normalized.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)
            || major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, isPrerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison == 0) comparison = Minor.CompareTo(other.Minor);
        if (comparison == 0) comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (IsPrerelease == other.IsPrerelease) return 0;
        return IsPrerelease ? -1 : 1;
    }
}
