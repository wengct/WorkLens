namespace WorkLens.Tests;

public sealed class SourceSetupUxTests
{
    [Fact]
    public async Task Saving_source_automatically_validates_and_offers_revalidation()
    {
        var razor = await ReadSourcesPageAsync();
        var saveStart = razor.IndexOf("private async Task SaveAndValidateSourceAsync()", StringComparison.Ordinal);
        var saveEnd = razor.IndexOf("private async Task BeginEditAsync", saveStart, StringComparison.Ordinal);
        var save = razor[saveStart..saveEnd];

        Assert.True(save.IndexOf("await SourceConfig.SaveAsync(source);", StringComparison.Ordinal)
            < save.IndexOf("await ValidateAsync(source.Id);", StringComparison.Ordinal));
        Assert.Contains("await ValidateAsync(source.Id);", save, StringComparison.Ordinal);
        Assert.Contains("\"重新驗證\"", razor, StringComparison.Ordinal);
        Assert.Contains("disabled=\"@isSavingSource\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("請按「驗證」", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("儲存後按「驗證」", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_uses_content_and_location_instead_of_platform_cartesian_options()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("要收集什麼？", razor, StringComparison.Ordinal);
        Assert.Contains("資料位於哪裡？", razor, StringComparison.Ordinal);
        Assert.Contains("Git repository", razor, StringComparison.Ordinal);
        Assert.Contains("Codex 工作紀錄", razor, StringComparison.Ordinal);
        Assert.Contains("Claude Code 工作紀錄", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"WindowsGit\">", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"MacOsGit\">", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_makes_optional_configuration_progressive()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("來源名稱（選填）", razor, StringComparison.Ordinal);
        Assert.Contains("<details class=\"source-advanced full\">", razor, StringComparison.Ordinal);
        Assert.Contains("進階設定", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_uses_pills_for_every_multi_value_source_field()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("repositoryPaths", razor, StringComparison.Ordinal);
        Assert.Contains("authorEmails", razor, StringComparison.Ordinal);
        Assert.Contains("vsCodeWorkspaceStoragePaths", razor, StringComparison.Ordinal);
        Assert.Contains("visualStudioSolutionPaths", razor, StringComparison.Ordinal);
        Assert.Contains("CommitVsCodeWorkspaceStoragePathInput", razor, StringComparison.Ordinal);
        Assert.Contains("CommitVisualStudioSolutionPathInput", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("newVsCodePaths", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("newVisualStudioPaths", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_lists_azure_devops_cli_extension_and_uses_cascading_scope_fields()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("Azure DevOps</option>", razor, StringComparison.Ordinal);
        Assert.Contains("az extension add --name azure-devops", razor, StringComparison.Ordinal);
        Assert.Contains("az login", razor, StringComparison.Ordinal);
        Assert.Contains("az ad signed-in-user show", razor, StringComparison.Ordinal);
        Assert.Contains("ADO Project", razor, StringComparison.Ordinal);
        Assert.Contains("target branch", razor, StringComparison.Ordinal);
        Assert.Contains("WorkLens Project", razor, StringComparison.Ordinal);
        Assert.Contains("收集 Work Item", razor, StringComparison.Ordinal);
        Assert.Contains("自己撰寫或修改的 Discussion", razor, StringComparison.Ordinal);
        Assert.Contains("＋新增一組", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("Organization：無法存取", razor, StringComparison.Ordinal);
        Assert.Contains("Projects：", razor, StringComparison.Ordinal);
        Assert.Contains("visualstudio.com", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_overview_keeps_ai_sharing_visible_and_action_menu_closable()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("@onclick=\"() => ToggleSourceAiAsync(source)\"", razor, StringComparison.Ordinal);
        Assert.Contains("<details class=\"source-more-actions\" data-action-menu>", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("<button class=\"button button-secondary button-small\" @onclick=\"() => ToggleSourceAiAsync(source)\" disabled=\"@sourceIsBusy\">@(source.IncludeInAi ? \"停止提供給 AI\" : \"允許提供給 AI\")</button>",
            razor[razor.IndexOf("<details class=\"source-more-actions\"", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
        Assert.Contains("data-menu-action", razor, StringComparison.Ordinal);
    }

    private static async Task<string> ReadSourcesPageAsync()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "Pages", "Sources.razor"));
        return await File.ReadAllTextAsync(path);
    }
}
