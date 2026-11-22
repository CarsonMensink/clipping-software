using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// Decides what the overlay window shows and when it can be hidden.
/// </summary>
/// <remarks>
/// Only the newest notification matters: one that arrives before the page is ready replaces any
/// already waiting, and a page that goes idle while a newer card is on its way is ignored.
/// Not thread-safe; use it from the overlay's UI thread.
/// </remarks>
internal sealed class OverlayState
{
    private bool _ready;
    private OverlayNotification? _waiting;
    private long _lastId;
    private long? _onScreen;

    /// <summary>
    /// Records a request to show a notification.
    /// </summary>
    /// <returns>The notification to send to the page now, or <see langword="null"/> to hold it until the page is ready.</returns>
    public OverlayNotification? Request(string title, string message, NotificationKind kind)
    {
        var notification = new OverlayNotification(++_lastId, title, message, kind);
        if (!_ready)
        {
            _waiting = notification;
            return null;
        }

        _onScreen = notification.Id;
        return notification;
    }

    /// <summary>
    /// Records that the page has loaded and is listening.
    /// </summary>
    /// <returns>The newest notification held while it was loading, if any, to send now.</returns>
    public OverlayNotification? PageReady()
    {
        if (_ready)
        {
            return null;
        }

        _ready = true;
        var waiting = _waiting;
        _waiting = null;
        _onScreen = waiting?.Id;
        return waiting;
    }

    /// <summary>
    /// Records that the page is gone, for example because its process crashed. Notifications are
    /// held again until it reports ready.
    /// </summary>
    public void PageLost()
    {
        _ready = false;
        _onScreen = null;
    }

    /// <summary>
    /// Records that the page finished showing card <paramref name="id"/>.
    /// </summary>
    /// <returns>Whether the window can be hidden: only if no newer card has been sent since.</returns>
    public bool PageIdle(long id)
    {
        if (_onScreen != id)
        {
            return false;
        }

        _onScreen = null;
        return true;
    }

    /// <summary>
    /// Records that the window was hidden without the page saying so, such as after a timeout.
    /// </summary>
    public void Hidden() => _onScreen = null;
}
