using System.Reflection;
using Microsoft.JSInterop;
using WorkLens.Components.Layout;

namespace WorkLens.Tests;

public sealed class EncouragementCatTests
{
    [Fact]
    public async Task Prerender_disposal_does_not_require_javascript()
    {
        await new EncouragementCat().DisposeAsync();
    }

    [Fact]
    public async Task Closing_a_disconnected_tab_does_not_throw_during_cleanup()
    {
        var component = new EncouragementCat();
        typeof(EncouragementCat).GetField("module", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, new DisconnectedModule());

        await component.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_during_initial_import_does_not_break_the_layout()
    {
        var component = new EncouragementCat();
        typeof(EncouragementCat).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, new DisconnectedRuntime());
        var render = typeof(EncouragementCat).GetMethod("OnAfterRenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)render.Invoke(component, [true])!;
        await component.DisposeAsync();
    }

    private sealed class DisconnectedModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSDisconnectedException("The test circuit has disconnected.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisconnectedRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSDisconnectedException("The test circuit has disconnected.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
