using System.Reflection;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using WorkLens.Components.Pages;
using WorkLens.Domain;

namespace WorkLens.Tests;

public sealed class AnnualReviewsRenderingTests
{
    [Fact]
    public async Task Annual_ai_forwards_live_service_progress_to_the_waiting_overlay()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "Pages", "AnnualReviews.razor"));
        var razor = await File.ReadAllTextAsync(path);

        Assert.Contains("new Progress<string>", razor, StringComparison.Ordinal);
        Assert.Contains("aiProgress = message", razor, StringComparison.Ordinal);
        Assert.Contains("progress: progress", razor, StringComparison.Ordinal);
        Assert.Contains("Detail=\"@aiProgress\"", razor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ai_loading_overlay_is_visible_only_while_processing(bool busy)
    {
        var page = new AnnualReviews();
        typeof(AnnualReviews).GetField("isGeneratingAi", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, busy);
        using var builder = new RenderTreeBuilder();
        typeof(AnnualReviews).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [builder]);
        var frames = builder.GetFrames();
#pragma warning disable BL0006
        var overlays = frames.Array.Take(frames.Count)
            .Where(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "Overlay")
            .ToList();
        Assert.Equal(busy ? 1 : 0, overlays.Count);
        if (busy) Assert.Equal(true, overlays[0].AttributeValue);
#pragma warning restore BL0006
    }

    [Fact]
    public void Existing_review_displays_formatted_dates_without_literal_format_suffixes()
    {
        var page = new AnnualReviews();
        typeof(AnnualReviews).GetField("reviews", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new List<AnnualReview>
            {
                new() { Name = "年度回顧", StartDate = new DateOnly(2025, 11, 3), EndDate = new DateOnly(2026, 9, 13) }
            });
        using var builder = new RenderTreeBuilder();
        typeof(AnnualReviews).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [builder]);
        var frames = builder.GetFrames();
        // Inspect the compiled page output without starting its database-backed lifecycle.
#pragma warning disable BL0006
        var output = string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => ""
        }));
#pragma warning restore BL0006

        Assert.DoesNotContain(":yyyy/MM/dd", output);
        Assert.Contains("2025/11/03－2026/09/13", output);
    }
}
