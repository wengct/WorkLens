using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkLens.Components;
using WorkLens.Domain;

namespace WorkLens.Tests;

public sealed class SourceStatusSummaryTests
{
    [Fact]
    public async Task No_sources_offers_setup_and_date_specific_recollection()
    {
        var html = await RenderAsync([], 0);
        Assert.Contains("新增資料來源", html);
        Assert.Contains("date=2026-09-07", html);
        Assert.Contains("此日期尚無已匯入", html);
    }

    [Fact]
    public async Task Disabled_sources_offer_management_without_discarding_existing_activity()
    {
        var html = await RenderAsync([new ActivitySource { Enabled = false }], 3);
        Assert.Contains("管理來源", html);
        Assert.Contains("已匯入 3 筆", html);
        Assert.DoesNotContain("此日期尚無已匯入", html);
    }

    [Fact]
    public async Task Mixed_sources_keep_failures_visible_even_when_activities_exist()
    {
        var html = await RenderAsync([
            new ActivitySource { DisplayName = "成功來源", Enabled = true, HealthStatus = SourceHealthStatus.Ready, LastSuccessAt = DateTimeOffset.UtcNow },
            new ActivitySource { DisplayName = "失敗來源", Enabled = true, HealthStatus = SourceHealthStatus.Error },
            new ActivitySource { DisplayName = "收集來源", Enabled = true, HealthStatus = SourceHealthStatus.Running },
            new ActivitySource { DisplayName = "新來源", Enabled = true, HealthStatus = SourceHealthStatus.Ready },
            new ActivitySource { DisplayName = "不可用來源", Enabled = true, HealthStatus = SourceHealthStatus.Unavailable }
        ], 4);
        Assert.Contains("查看並修正", html);
        Assert.Contains("正在收集", html);
        Assert.Contains("前往執行首次收集", html);
        Assert.Contains("來源目前不可用", html);
        Assert.Contains("不代表此日期已完整收集", html);
    }

    private static async Task<string> RenderAsync(IReadOnlyList<ActivitySource> sources, int count)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SourceStatusSummary>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Sources"] = sources, ["Date"] = new DateOnly(2026, 9, 7), ["ActivityCount"] = count
            }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
