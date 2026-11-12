using System.Reflection;
using Clipping.Core.Shell;
using Clipping.Windows.Shell.Overlay;
using Microsoft.Extensions.Logging;

namespace Clipping.Windows.Shell;

/// <summary>
/// Hosts the application in the notification area: the tray icon, its menu, the on-screen
/// overlay that shows notifications, and the hidden window whose thread runs the message loop.
/// </summary>
/// <remarks>
/// Create, run, and dispose it on one STA thread. Features receive only the menu or
/// notification interface they need.
/// </remarks>
internal sealed class TrayShell : IDisposable
{
    // Sorts after every MenuGroup, so Exit is always last.
    private const MenuGroup ExitGroup = (MenuGroup)int.MaxValue;

    // NotifyIcon opens its menu only on right-click; this private method is what it calls then.
    // If a WinForms update removes it, left-click simply does nothing.
    private static readonly MethodInfo? ShowContextMenu =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly MessageWindow _window;
    private readonly ContextMenuStrip _menuStrip;
    private readonly NotifyIcon _icon;
    private readonly OverlayNotifications _overlay;

    /// <param name="overlayMonitor">The monitor to show notifications on, in virtual-desktop pixels.</param>
    /// <param name="webViewDataFolder">Where the web UI keeps its browser profile.</param>
    /// <param name="loggerFactory">Creates the loggers for the shell's parts.</param>
    public TrayShell(Func<Rectangle> overlayMonitor, string webViewDataFolder, ILoggerFactory loggerFactory)
    {
        _window = new MessageWindow();
        _menuStrip = new ContextMenuStrip();
        Menu = new TrayMenu(() => _menuStrip.Items, _window);

        // TODO: replace with the application's own icon once there is one.
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Clipping",
            ContextMenuStrip = _menuStrip,
            Visible = true,
        };
        _icon.MouseClick += OnIconClicked;

        _overlay = new OverlayNotifications(
            _window, overlayMonitor, webViewDataFolder, loggerFactory.CreateLogger<OverlayNotifications>());

        Menu.Add("Exit", ExitGroup, RequestExit);
    }

    public ITrayMenu Menu { get; }

    public INotificationService Notifications => _overlay;

    // The recorder stays alive until the tray Exit action ends this message loop.
    public void Run() => Application.Run();

    // Queue Exit after earlier UI actions so shutdown begins on the shell thread.
    private void RequestExit() => _window.Post(Application.ExitThread);

    /// <summary>
    /// Removes the tray icon and destroys the window. Call on the shell's thread after
    /// <see cref="Run"/> returns.
    /// </summary>
    public void Dispose()
    {
        _overlay.Dispose();

        // Without this, the icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menuStrip.Dispose();
        _window.Dispose();
    }

    private void OnIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowContextMenu?.Invoke(_icon, null);
        }
    }
}
