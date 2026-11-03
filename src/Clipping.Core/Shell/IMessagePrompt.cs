namespace Clipping.Core.Shell;

/// <summary>
/// Tells the user something they must acknowledge, such as why their choice was rejected.
/// </summary>
/// <remarks>
/// Use after the user acted from the tray. For anything unprompted, use
/// <see cref="INotificationService"/>, which does not take focus.
/// </remarks>
public interface IMessagePrompt
{
    /// <summary>
    /// Shows the message and waits for the user to dismiss it.
    /// </summary>
    /// <remarks>Callable from any thread.</remarks>
    Task ShowAsync(string title, string message, NotificationKind kind);
}
