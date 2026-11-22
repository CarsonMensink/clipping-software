using Clipping.Core.Clips;
using Clipping.Core.Shell;

namespace Clipping.Core.Tests.Clips;

public sealed class ClipSaveNotifierTests
{
    // The user can tell the hotkey worked and find the file: its application folder and name (#12).
    [Fact]
    public void Notify_SavedClip_ShowsInfoNamingTheFileAndItsApplicationFolder()
    {
        // Arrange
        var notifications = new FakeNotifications();
        var notifier = new ClipSaveNotifier(notifications);
        var path = Path.Combine("C:", "Videos", "Cyberpunk 2077", "2026-10-31 22-16-05.mp4");

        // Act
        notifier.Notify(ClipSaveResult.Saved(path, TimeSpan.FromSeconds(30)));

        // Assert
        var (title, message, kind) = Assert.Single(notifications.Shown);
        Assert.Equal("Clip saved", title);
        Assert.Equal(NotificationKind.Info, kind);
        Assert.Contains(Path.Combine("Cyberpunk 2077", "2026-10-31 22-16-05.mp4"), message);
    }

    // A failed save must look different from a successful one and say why (#12).
    [Fact]
    public void Notify_FailedSave_ShowsErrorWithTheReason()
    {
        // Arrange
        var notifications = new FakeNotifications();
        var notifier = new ClipSaveNotifier(notifications);

        // Act
        notifier.Notify(ClipSaveResult.Failed("The disk is full."));

        // Assert
        var (title, message, kind) = Assert.Single(notifications.Shown);
        Assert.NotEqual("Clip saved", title);
        Assert.Equal(NotificationKind.Error, kind);
        Assert.Contains("The disk is full.", message);
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<(string Title, string Message, NotificationKind Kind)> Shown { get; } = [];

        public void Show(string title, string message, NotificationKind kind) =>
            Shown.Add((title, message, kind));
    }
}
