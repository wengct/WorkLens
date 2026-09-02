using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class GitSourceAdapterValidationTests
{
    [Fact]
    public async Task Git_validation_requires_at_least_one_author_email()
    {
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WindowsGit,
            SettingsJson = SourceSettingsSerializer.Serialize(new GitSourceSettings
            {
                RepositoryPaths = [@"C:\Work\Repo"]
            })
        };
        var adapter = new GitSourceAdapter(new ProcessRunner(), ActivitySourceType.WindowsGit);

        var result = await adapter.ValidateAsync(source, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("email", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Git_validation_rejects_invalid_author_email()
    {
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WindowsGit,
            SettingsJson = SourceSettingsSerializer.Serialize(new GitSourceSettings
            {
                RepositoryPaths = [@"C:\Work\Repo"],
                AuthorEmails = ["not-an-email"]
            })
        };
        var adapter = new GitSourceAdapter(new ProcessRunner(), ActivitySourceType.WindowsGit);

        var result = await adapter.ValidateAsync(source, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("email", result.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
