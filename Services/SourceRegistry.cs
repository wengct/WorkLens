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
                    _ => x.Key.ToString()
                },
                x.Value.Capabilities))
            .OrderBy(x => x.SourceType)
            .ToList();
}
