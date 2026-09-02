using WorkLens.Domain;

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
}
