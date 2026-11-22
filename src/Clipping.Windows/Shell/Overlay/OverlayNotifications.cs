using System.Runtime.InteropServices;
using Clipping.Core.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Vortice.DirectComposition;

namespace Clipping.Windows.Shell.Overlay;

/// <summary>
/// Shows notifications as cards drawn over the screen by the web UI's overlay page.
/// </summary>
/// <remarks>
/// The page runs in WebView2, composed through DirectComposition into an <see cref="OverlayWindow"/>,
/// so its transparent areas show the game underneath. It is loaded once at startup and kept, so a
/// notification appears without waiting for a browser to start. The window is hidden whenever no
/// card is showing. Create and dispose on the shell's UI thread.
/// </remarks>
internal sealed class OverlayNotifications : INotificationService, IDisposable
{
    // The area the page has to draw a card in, in DIPs. The card sits in its top-right corner.
    private const int WidthDips = 420;
    private const int HeightDips = 240;

    private const string VirtualHost = "clipping.ui";

    // Hides the window if the page never reports idle, such as after a renderer crash.
    private static readonly TimeSpan IdleBackstop = TimeSpan.FromSeconds(10);

    private readonly MessageWindow _ui;
    private readonly Func<Rectangle> _monitorBounds;
    private readonly string _userDataFolder;
    private readonly ILogger _log;
    private readonly OverlayState _state = new();
    private readonly OverlayWindow _window;
    private readonly IDCompositionDevice _composition;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _root;
    private readonly System.Windows.Forms.Timer _backstop;

    private CoreWebView2CompositionController? _controller;
    private bool _disposed;

    /// <param name="ui">The shell's UI thread, which runs everything here.</param>
    /// <param name="monitorBounds">The monitor to show cards on, in virtual-desktop pixels. Called on the UI thread.</param>
    /// <param name="userDataFolder">Where WebView2 keeps its profile.</param>
    /// <param name="log">Receives startup and page failures.</param>
    public OverlayNotifications(MessageWindow ui, Func<Rectangle> monitorBounds, string userDataFolder, ILogger<OverlayNotifications> log)
    {
        _ui = ui;
        _monitorBounds = monitorBounds;
        _userDataFolder = userDataFolder;
        _log = log;

        _window = new OverlayWindow();

        // WebView2 brings its own rendering device, so this one needs none.
        _composition = DComp.DCompositionCreateDevice2<IDCompositionDevice>(null!);
        _composition.CreateTargetForHwnd(_window.Handle, true, out _target).CheckError();
        _composition.CreateVisual(out _root).CheckError();
        _target.SetRoot(_root);
        _composition.Commit();

        _backstop = new System.Windows.Forms.Timer { Interval = (int)IdleBackstop.TotalMilliseconds };
        _backstop.Tick += (_, _) =>
        {
            _state.Hidden();
            HideWindow();
        };

        // Started from the message loop, so WebView2's callbacks have a loop to arrive on.
        _ui.Post(() => _ = InitializeAsync());
    }

    /// <inheritdoc />
    public void Show(string title, string message, NotificationKind kind)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrEmpty(message);

        _ui.Post(() =>
        {
            if (!_disposed && _state.Request(title, message, kind) is { } notification)
            {
                Present(notification);
            }
        });
    }

    /// <summary>
    /// Closes the page and destroys the window. Call on the UI thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backstop.Dispose();
        _controller?.Close();
        _root.Dispose();
        _target.Dispose();
        _composition.Dispose();
        _window.Dispose();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            var controller = await environment.CreateCoreWebView2CompositionControllerAsync(_window.Handle);
            if (_disposed)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            controller.DefaultBackgroundColor = Color.Transparent;
            // The scale follows the monitor each card is shown on; see Present.
            controller.ShouldDetectMonitorScaleChanges = false;
            controller.RootVisualTarget = Marshal.GetObjectForIUnknown(_root.NativePointer);
            _composition.Commit();

            var page = controller.CoreWebView2;
            var devUrl = Environment.GetEnvironmentVariable("CLIPPING_UI_DEV_URL");
            var settings = page.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreDevToolsEnabled = devUrl is not null;

            page.WebMessageReceived += OnPageMessage;
            page.ProcessFailed += OnProcessFailed;

            if (devUrl is not null)
            {
                // The Vite dev server, for hot reload while working on the page.
                page.Navigate($"{devUrl.TrimEnd('/')}/overlay.html");
            }
            else
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "ui");
                page.SetVirtualHostNameToFolderMapping(VirtualHost, folder, CoreWebView2HostResourceAccessKind.Deny);
                page.Navigate($"https://{VirtualHost}/overlay.html");
            }
        }
        catch (Exception e)
        {
            // Includes a missing WebView2 runtime. Notifications are dropped from here on.
            _log.LogError(e, "Overlay could not start; notifications will not be shown.");
        }
    }

    private void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        switch (OverlayProtocol.ReadPageMessage(e.WebMessageAsJson))
        {
            case PageMessage.Ready:
                if (_state.PageReady() is { } waiting)
                {
                    Present(waiting);
                }

                break;

            case PageMessage.Idle idle when _state.PageIdle(idle.Id):
                HideWindow();
                break;
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.LogError("Overlay page process failed: {Kind}, {Reason}.", e.ProcessFailedKind, e.Reason);
        _state.PageLost();
        HideWindow();

        // The page reports ready again once it has reloaded.
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            _controller?.CoreWebView2.Reload();
        }
    }

    private void Present(OverlayNotification notification)
    {
        if (_controller is null)
        {
            return;
        }

        var monitor = _monitorBounds();
        var scale = MonitorScale(monitor);
        var width = (int)Math.Ceiling(WidthDips * scale);
        var height = (int)Math.Ceiling(HeightDips * scale);

        _controller.RasterizationScale = scale;
        _controller.Bounds = new Rectangle(0, 0, width, height);
        _window.ShowAt(new Rectangle(monitor.Right - width, monitor.Top, width, height));
        // The controller doesn't follow the window: it starts hidden, as the window was when it
        // was created, and a hidden page neither paints nor runs its animations.
        _controller.IsVisible = true;
        _controller.CoreWebView2.PostWebMessageAsJson(OverlayProtocol.Notify(notification));

        _backstop.Stop();
        _backstop.Start();
    }

    private void HideWindow()
    {
        _backstop.Stop();
        _window.Hide();

        // Lets the browser throttle the page while nothing is shown.
        if (_controller is not null)
        {
            _controller.IsVisible = false;
        }
    }

    // Effective DPI of the monitor, as a multiple of 96.
    private static double MonitorScale(Rectangle monitor)
    {
        const int MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;

        var center = new Point(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2);
        var handle = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
