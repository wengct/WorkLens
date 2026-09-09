using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkLens.Components;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AiEnvironmentDiagnosticsTests
{
    [Fact]
    public async Task Details_are_rendered_when_diagnostics_include_messages()
    {
        var detection = new AiDetectionResult(
            new AiProviderValidationResult(
                false,
                "MissingPrerequisite",
                "執行環境尚未完成。",
                Details: ["找不到 npx。", "找不到 Google Chrome。"]),
            "/usr/local/bin/ask-bridge",
            "1.2.3",
            true,
            false);

        var html = await RenderAsync(detection);

        Assert.Contains("class=\"hint ai-diagnostic-details\"", html, StringComparison.Ordinal);
        Assert.Contains("<li>找不到 npx。</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>找不到 Google Chrome。</li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Details_list_is_omitted_when_diagnostics_have_no_messages()
    {
        var detection = new AiDetectionResult(
            new AiProviderValidationResult(true, "Ready", "執行環境已就緒。", Details: []),
            "/usr/local/bin/ask-bridge",
            "1.2.3",
            true,
            true);

        var html = await RenderAsync(detection);

        Assert.DoesNotContain("ai-diagnostic-details", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(AiDetectionResult detection)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AiEnvironmentDiagnostics>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(AiEnvironmentDiagnostics.Detection)] = detection
                }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
