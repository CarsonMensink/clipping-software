using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// Shows notifications as the tray icon's balloon tips, which Windows 10 and later render as
/// toasts that do not take focus.
/// </summary>
internal sealed class BalloonNotifications : INotificationService
{
    // Ignored since Windows Vista, which times balloons by the accessibility setting, but required.
    private const int TimeoutMilliseconds = 5000;

    private readonly NotifyIcon _icon;
    private readonly MessageWindow _window;

    // The newest notification's handler; UI thread only. BalloonTipClicked carries no balloon
    // identity, so every click runs this, whichever balloon it was on.
    private Action? _onClicked;

    public BalloonNotifications(NotifyIcon icon, MessageWindow window)
    {
        _icon = icon;
        _window = window;
        _icon.BalloonTipClicked += (_, _) => _onClicked?.Invoke();
    }

    /// <inheritdoc />
    public void Show(string title, string message, NotificationKind kind, Action? onClicked = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrEmpty(message);

        var icon = kind == NotificationKind.Error ? ToolTipIcon.Error : ToolTipIcon.Info;
        _window.Post(() =>
        {
            _onClicked = onClicked;
            _icon.ShowBalloonTip(TimeoutMilliseconds, title, message, icon);
        });
    }
}
