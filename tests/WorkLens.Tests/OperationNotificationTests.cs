using System.Reflection;
using Microsoft.JSInterop;
using WorkLens.Components;

namespace WorkLens.Tests;

public sealed class OperationNotificationTests
{
    [Fact]
    public async Task Prerender_disposal_does_not_call_javascript()
    {
        var js = new RecordingRuntime();
        await Create(js).DisposeAsync();
        var preferences = new NotificationPreferences();
        typeof(NotificationPreferences).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(preferences, js);
        await preferences.DisposeAsync();
        Assert.Empty(js.Calls);
    }

    [Fact]
    public async Task Component_reports_transitions_once_and_removes_attention_when_disposed()
    {
        var js = new RecordingRuntime();
        var component = Create(js);
        await RenderAsync(component);
        Assert.Empty(js.Calls);

        Set(component, nameof(OperationNotification.Active), true);
        await RenderAsync(component);
        await RenderAsync(component);
        Assert.Single(js.Calls);
        Assert.Equal("workLensNotifications.start", js.Calls[0].Identifier);

        Set(component, nameof(OperationNotification.Active), false);
        Set(component, nameof(OperationNotification.Outcome), "partial");
        await RenderAsync(component);
        await RenderAsync(component);
        Assert.Equal(2, js.Calls.Count);
        Assert.Equal("partial", js.Calls[1].Args![1]);

        await component.DisposeAsync();
        Assert.Equal("workLensNotifications.remove", js.Calls[2].Identifier);
        Assert.Equal(js.Calls[0].Args![0], js.Calls[2].Args![0]);
        Set(component, nameof(OperationNotification.Active), true);
        await RenderAsync(component);
        Assert.Equal(3, js.Calls.Count);
    }

    [Fact]
    public async Task Unavailable_browser_notifications_do_not_break_operation_or_disposal()
    {
        var component = Create(new RecordingRuntime(fail: true));
        Set(component, nameof(OperationNotification.Active), true);
        await RenderAsync(component);
        Set(component, nameof(OperationNotification.Active), false);
        await RenderAsync(component);
        await component.DisposeAsync();
    }

    private static OperationNotification Create(IJSRuntime js)
    {
        var component = new OperationNotification();
        Set(component, "JS", js);
        Set(component, nameof(OperationNotification.Label), "摘要");
        return component;
    }

    private static void Set(OperationNotification component, string name, object value) =>
        typeof(OperationNotification).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(component, value);

    private static Task RenderAsync(OperationNotification component) =>
        (Task)typeof(OperationNotification).GetMethod("OnAfterRenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component, [false])!;

    private sealed class RecordingRuntime(bool fail = false) : IJSRuntime
    {
        public List<(string Identifier, object?[]? Args)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (fail) throw new JSDisconnectedException("Disconnected");
            Calls.Add((identifier, args));
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
