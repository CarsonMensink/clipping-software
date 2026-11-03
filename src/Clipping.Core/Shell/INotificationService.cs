namespace Clipping.Core.Shell;

/// <summary>
/// Shows short notifications from the tray without taking focus.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Shows a notification, replacing any still on screen.
    /// </summary>
    /// <param name="title">The heading, such as <c>Clip saved</c>.</param>
    /// <param name="message">The detail, such as the file name. Must not be empty.</param>
    /// <param name="kind">Whether it reports a failure, which changes its icon.</param>
    /// <param name="onClicked">
    /// Runs on the UI thread when the user clicks a notification. Clicks do not say which
    /// notification was clicked, so a click on an older one in the notification center runs
    /// the newest notification's handler.
    /// </param>
    /// <remarks>
    /// Callable from any thread. Windows may hold it back, for example while Do Not Disturb
    /// is on.
    /// </remarks>
    void Show(string title, string message, NotificationKind kind, Action? onClicked = null);
}
