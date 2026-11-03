using System.Reflection;
using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// Hosts the application in the notification area: the tray icon, its menu, notifications,
/// prompts, and the hidden window whose thread runs the message loop.
/// </summary>
/// <remarks>
/// The only component that touches WinForms UI. Create, run, and dispose it on one STA thread.
/// Features receive the interface they need: <see cref="Menu"/>, <see cref="Notifications"/>,
/// <see cref="Prompts"/>, or <see cref="MessageWindow"/>.
/// </remarks>
internal sealed class TrayShell : IAppShell, IDisposable
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

    public TrayShell()
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

        Notifications = new BalloonNotifications(_icon, _window);
        Prompts = new MessageBoxPrompt(_window);

        Menu.Add("Exit", ExitGroup, RequestExit);
    }

    public ITrayMenu Menu { get; }

    public INotificationService Notifications { get; }

    public IMessagePrompt Prompts { get; }

    public IMessageWindow MessageWindow => _window;

    /// <inheritdoc />
    public void Run() => Application.Run();

    /// <inheritdoc />
    public void RequestExit() => _window.Post(Application.ExitThread);

    /// <summary>
    /// Removes the tray icon and destroys the window. Call on the shell's thread after
    /// <see cref="Run"/> returns.
    /// </summary>
    public void Dispose()
    {
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
