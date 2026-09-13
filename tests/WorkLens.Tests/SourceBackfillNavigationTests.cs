using System.Reflection;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using WorkLens.Components.Pages;

namespace WorkLens.Tests;

public sealed class SourceBackfillNavigationTests
{
    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, true, 1)]
    [InlineData(true, true, false, 1)]
    public async Task Navigation_requires_confirmation_only_during_backfill(
        bool running, bool confirm, bool prevented, int expectedPrompts)
    {
        var js = new ConfirmationRuntime(confirm);
        var page = CreatePage(running, js);
        var context = new LocationChangingContext { TargetLocation = "http://localhost/reports" };

        await NavigateAsync(page, context);

        Assert.Equal(prevented, IsPrevented(context));
        Assert.Equal(expectedPrompts, js.Prompts);
        await page.DisposeAsync();
    }

    [Fact]
    public async Task Failed_confirmation_keeps_the_user_on_the_backfill_page()
    {
        var page = CreatePage(true, new ConfirmationRuntime(false, fail: true));
        var context = new LocationChangingContext { TargetLocation = "http://localhost/reports" };

        await NavigateAsync(page, context);

        Assert.True(IsPrevented(context));
        await page.DisposeAsync();
    }

    private static bool IsPrevented(LocationChangingContext context) =>
        (bool)typeof(LocationChangingContext).GetProperty("DidPreventNavigation",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;

    private static Sources CreatePage(bool running, IJSRuntime js)
    {
        var page = new Sources();
        typeof(Sources).GetField("isBackfilling", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, running);
        typeof(Sources).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, js);
        return page;
    }

    private static Task NavigateAsync(Sources page, LocationChangingContext context) =>
        (Task)typeof(Sources).GetMethod("BeforeNavigationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [context])!;

    private sealed class ConfirmationRuntime(bool confirmed, bool fail = false) : IJSRuntime
    {
        public int Prompts { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("confirm", identifier);
            Prompts++;
            if (fail) throw new JSException("Confirmation unavailable");
            return ValueTask.FromResult((TValue)(object)confirmed);
        }
    }
}
