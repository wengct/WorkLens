using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ToastServiceTests
{
    [Fact]
    public void Success_and_error_notifications_keep_their_visual_level()
    {
        var service = new ToastService();
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
        var service = new ToastService();
        var count = 0;
        service.NotificationRequested += _ => count++;

        service.Success("  ");

        Assert.Equal(0, count);
    }
}
