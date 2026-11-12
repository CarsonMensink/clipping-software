namespace Clipping.Core.Shell;

/// <summary>
/// Shows short notifications over the screen without taking focus.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Shows a notification, replacing any still on screen.
    /// </summary>
    /// <param name="title">The heading, such as <c>Clip saved</c>.</param>
    /// <param name="message">The detail, such as the file name. Must not be empty.</param>
    /// <param name="kind">Whether it reports a failure, which changes how it looks.</param>
    /// <remarks>
    /// Callable from any thread. Notifications cannot be clicked: they never take input from the
    /// application underneath.
    /// </remarks>
    void Show(string title, string message, NotificationKind kind);
}
