using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SourceSettingsTests
{
    [Fact]
    public void Git_source_settings_round_trip_without_secrets()
    {
        var settings = new GitSourceSettings
        {
            RepositoryPaths = [@"C:\Work\Repo", "/mnt/c/Work/Repo"],
            Distro = "Ubuntu",
            AuthorEmails = ["me@work.example", "me@personal.example"]
        };

        var json = SourceSettingsSerializer.Serialize(settings);
        var restored = SourceSettingsSerializer.DeserializeGit(json);

        Assert.Equal(settings.RepositoryPaths, restored.RepositoryPaths);
        Assert.Equal(settings.Distro, restored.Distro);
        Assert.Equal(settings.AuthorEmails, restored.AuthorEmails);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Author_emails_are_normalized_and_deduplicated()
    {
        var emails = SourceSettingsSerializer.NormalizeAuthorEmails([
            " Me@Work.Example ",
            "me@work.example",
            "other@example.com",
            ""
        ]);

        Assert.Equal(["Me@Work.Example", "other@example.com"], emails);
    }

    [Fact]
    public void Codex_source_settings_round_trip()
    {
        var settings = new CodexSourceSettings
        {
            CodexHome = @"C:\Users\me\.codex",
            Distro = "Ubuntu"
        };

        var json = SourceSettingsSerializer.Serialize(settings);
        var restored = SourceSettingsSerializer.DeserializeCodex(json);

        Assert.Equal(settings.CodexHome, restored.CodexHome);
        Assert.Equal(settings.Distro, restored.Distro);
    }

    [Fact]
    public void Claude_code_source_settings_round_trip()
    {
        var settings = new ClaudeCodeSourceSettings
        {
            ClaudeCodeHome = @"C:\Users\me\.claude",
            Distro = "Ubuntu"
        };

        var json = SourceSettingsSerializer.Serialize(settings);
        var restored = SourceSettingsSerializer.DeserializeClaudeCode(json);

        Assert.Equal(settings.ClaudeCodeHome, restored.ClaudeCodeHome);
        Assert.Equal(settings.Distro, restored.Distro);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Azure_devops_source_settings_normalize_org_branch_and_round_trip()
    {
        var settings = new AzureDevOpsSourceSettings
        {
            OrganizationUrl = " https://dev.azure.com/example/ ",
            Scopes =
            [
                new AzureDevOpsPullRequestScope
                {
                    ProjectId = " project-id ",
                    ProjectName = "Project",
                    RepositoryId = "repo-id",
                    RepositoryName = "Repo",
                    TargetBranch = "refs/heads/main",
                    WorkLensProjectId = Guid.NewGuid()
                }
            ]
        };

        settings.OrganizationUrl = SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(settings.OrganizationUrl);
        settings.Scopes[0].TargetBranch = SourceSettingsSerializer.NormalizeAzureDevOpsBranch(settings.Scopes[0].TargetBranch);
        var restored = SourceSettingsSerializer.DeserializeAzureDevOps(SourceSettingsSerializer.Serialize(settings));

        Assert.Equal("https://dev.azure.com/example", restored.OrganizationUrl);
        Assert.Equal("main", restored.Scopes[0].TargetBranch);
        Assert.Equal(SourceSettingsSerializer.AzureDevOpsScopeKey(settings.Scopes[0]),
            SourceSettingsSerializer.AzureDevOpsScopeKey(restored.Scopes[0]));
    }

    [Theory]
    [InlineData("https://dev.azure.com/example", true)]
    [InlineData("https://example.visualstudio.com", true)]
    [InlineData("http://dev.azure.com/example", false)]
    [InlineData("https://example.invalid", false)]
    public void Azure_devops_organization_url_validation_is_explicit(string value, bool expected)
    {
        Assert.Equal(expected, AzureDevOpsCliService.IsSupportedOrganizationUrl(value));
    }

    [Fact]
    public void Azure_devops_html_is_safely_reduced_to_plain_text()
    {
        Assert.Equal("Details & more", AzureDevOpsCliService.ToPlainText("<p>Details &amp; more</p>"));
    }
}
