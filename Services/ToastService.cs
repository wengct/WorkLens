namespace WorkLens.Services;

public sealed class ToastService
{
    public event Action<ToastNotification>? NotificationRequested;

    public void Success(string message) => Show(message, ToastLevel.Success);

    public void Error(string message) => Show(message, ToastLevel.Error);

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
