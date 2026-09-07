using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using WorkLens.Components;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SensitiveContentPreviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_highlights_only_current_redactions_and_encodes_untrusted_content(bool hasPreviewValues)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(new UnusedJsRuntime());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SensitiveContentPreview>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(SensitiveContentPreview.InputMarkdown)] = "<script>alert(1)</script>[已遮蔽：個人資料]尾端",
                    [nameof(SensitiveContentPreview.EffectivePrompt)] = "[已遮蔽：機敏憑證][已遮蔽：自訂敏感詞]",
                    [nameof(SensitiveContentPreview.PreviewValues)] = hasPreviewValues ? new AiRedactionPreview[]
                    {
                        new("工作資料", "<script>alert(1)</script>".Length, "person@example.invalid"),
                        new("Prompt", 0, "<secret>\"value\"")
                    } : Array.Empty<AiRedactionPreview>()
                }));
            return output.ToHtmlString();
        });

        Assert.Equal(hasPreviewValues ? 2 : 0, html.Split("class=\"sensitive-marker\"").Length - 1);
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("[已遮蔽：機敏憑證]", decoded);
        Assert.Contains("[已遮蔽：個人資料]", decoded);
        Assert.Contains("[已遮蔽：自訂敏感詞]</pre>", decoded);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("data-sensitive-previous", html);
        Assert.Contains("data-sensitive-next", html);
        if (hasPreviewValues) Assert.Contains("&lt;secret&gt;&quot;value&quot;", html);
        Assert.DoesNotContain("<secret>", html);
        Assert.Equal(hasPreviewValues ? 2 : 0, html.Split("title=\"").Length - 1);
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
