using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AiProviderRegistry(IEnumerable<IAiProviderAdapter> adapters)
{
    private readonly IReadOnlyDictionary<string, IAiProviderAdapter> adapters = adapters
        .ToDictionary(adapter => adapter.ProviderType, StringComparer.OrdinalIgnoreCase);

    public IAiProviderAdapter Get(string providerType) =>
        adapters.TryGetValue(providerType, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"尚未註冊 AI Provider Adapter：{providerType}");

    public IAiProviderAdapter Get(AiProviderConfiguration configuration) =>
        Get(configuration.ProviderType);
}
