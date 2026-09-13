using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ToastServiceTests
{
    [Fact]
    public void Toast_and_inline_errors_log_without_logging_success_or_cancellation_notices()
    {
        var logger = new RecordingLogger();
        var service = new ToastService(logger);
        var notifications = new List<ToastNotification>();
        service.NotificationRequested += notifications.Add;
        var exception = new InvalidOperationException("failure");
        service.Error("AI 回覆的 reportId 不是有效 GUID", exception, "Generate", "AnnualReviews.razor");
        service.RecordError("草稿暫存失敗", operation: "Save", file: "WorkDraftEditor.razor");
        service.Success("完成");
        service.Info("已中斷");
        service.Error(" ");
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, item.Level));
        Assert.Contains("AnnualReviews.Generate", logger.Entries[0].Message);
        Assert.Same(exception, logger.Entries[0].Exception);
        Assert.Equal(3, notifications.Count);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<ToastService>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((level, formatter(state, exception), exception));
    }

    [Fact]
    public void Success_and_error_notifications_keep_their_visual_level()
    {
        var service = new ToastService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ToastService>.Instance);
        var notifications = new List<ToastNotification>();
        service.NotificationRequested += notifications.Add;

        service.Success("儲存成功");
        service.Error("儲存失敗");

        Assert.Collection(
            notifications,
            notification =>
            {
                Assert.Equal("儲存成功", notification.Message);
                Assert.Equal(ToastLevel.Success, notification.Level);
            },
            notification =>
            {
                Assert.Equal("儲存失敗", notification.Message);
                Assert.Equal(ToastLevel.Error, notification.Level);
            });
    }

    [Fact]
    public void Empty_notifications_are_ignored()
    {
        var service = new ToastService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ToastService>.Instance);
        var count = 0;
        service.NotificationRequested += _ => count++;

        service.Success("  ");

        Assert.Equal(0, count);
    }
}
