using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SourceRegistry(IEnumerable<IActivitySourceAdapter> adapters)
{
    private readonly IReadOnlyDictionary<ActivitySourceType, IActivitySourceAdapter> adapters =
        adapters.ToDictionary(x => Enum.Parse<ActivitySourceType>(x.SourceType, ignoreCase: true));

    public IActivitySourceAdapter Get(ActivitySourceType sourceType) =>
        adapters.TryGetValue(sourceType, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"尚未註冊來源 Adapter：{sourceType}");

    public IReadOnlyList<SourceDefinition> GetDefinitions() =>
        adapters
            .Select(x => new SourceDefinition(
                x.Key,
                x.Key switch
                {
                    ActivitySourceType.WindowsGit => "Windows Git",
                    ActivitySourceType.WslGit => "WSL Git",
                    ActivitySourceType.WindowsCodex => "Windows Codex",
                    ActivitySourceType.WslCodex => "WSL Codex",
                    ActivitySourceType.MacOsGit => "macOS Git",
                    ActivitySourceType.MacOsCodex => "macOS Codex",
                    ActivitySourceType.AzureDevOpsPullRequest => "Azure DevOps PR",
                    ActivitySourceType.WindowsClaudeCode => "Windows Claude Code",
                    ActivitySourceType.WslClaudeCode => "WSL Claude Code",
                    ActivitySourceType.MacOsClaudeCode => "macOS Claude Code",
                    ActivitySourceType.WindowsCopilot => "Windows GitHub Copilot CLI／App",
                    ActivitySourceType.WslCopilot => "WSL GitHub Copilot CLI／App",
                    ActivitySourceType.MacOsCopilot => "macOS GitHub Copilot CLI／App",
                    ActivitySourceType.WindowsVsCodeCopilot => "Windows VS Code Copilot Chat",
                    ActivitySourceType.WslVsCodeCopilot => "WSL VS Code Copilot Chat",
                    ActivitySourceType.MacOsVsCodeCopilot => "macOS VS Code Copilot Chat",
                    ActivitySourceType.WindowsVisualStudioCopilot => "Visual Studio Copilot Chat",
                    _ => x.Key.ToString()
                },
                x.Value.Capabilities))
            .OrderBy(x => x.SourceType)
            .ToList();
}
