using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AiProviderRegistryTests
{
    [Fact]
    public void Get_selects_adapter_by_provider_type_without_case_sensitivity()
    {
        var adapter = new StubProviderAdapter("ask-bridge");
        var registry = new AiProviderRegistry([adapter]);

        Assert.Same(adapter, registry.Get("ASK-BRIDGE"));
        Assert.Same(adapter, registry.Get(new AiProviderConfiguration { ProviderType = "ask-bridge" }));
    }

    [Fact]
    public void Get_rejects_an_unregistered_provider_type()
    {
        var registry = new AiProviderRegistry([new StubProviderAdapter("ask-bridge")]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Get("openai"));

        Assert.Contains("openai", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubProviderAdapter(string providerType) : IAiProviderAdapter
    {
        public string ProviderType => providerType;

        public Task<AiProviderValidationResult> ValidateAsync(
            AiProviderConfiguration configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiProviderValidationResult(true, "Ready", "Ready"));

        public Task<AiReportResult> GenerateAsync(
            AiProviderConfiguration configuration,
            AiReportRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiReportResult(true, "Body", null, null));

        public Task<AiConnectionTestResult> TestConnectionAsync(
            AiProviderConfiguration configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiConnectionTestResult(true, "OK", null));
    }
}
