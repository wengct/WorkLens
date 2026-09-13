using System.Runtime.CompilerServices;

namespace WorkLens.Services;

public sealed class ToastService(ILogger<ToastService> logger)
{
    public event Action<ToastNotification>? NotificationRequested;

    public void Success(string message) => Show(message, ToastLevel.Success);

    public void Error(string message, Exception? exception = null,
        [CallerMemberName] string operation = "", [CallerFilePath] string file = "")
    {
        RecordError(message, exception, operation, file);
        Show(message, ToastLevel.Error);
    }

    // Inline errors use the same logging path without creating a second notification.
    public void RecordError(string message, Exception? exception = null,
        [CallerMemberName] string operation = "", [CallerFilePath] string file = "")
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        logger.LogError(exception, "UI error in {Component}.{Operation}: {Message}",
            Path.GetFileNameWithoutExtension(file), operation, message);
    }

    public void Info(string message) => Show(message, ToastLevel.Info);

    private void Show(string message, ToastLevel level)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        NotificationRequested?.Invoke(new ToastNotification(Guid.NewGuid(), message, level));
    }
}

public sealed record ToastNotification(Guid Id, string Message, ToastLevel Level);

public enum ToastLevel
{
    Success,
    Error,
    Info
}
