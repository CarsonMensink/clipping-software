using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Clipping.Windows.Shell.Overlay;

/// <summary>
/// A borderless top-level window for drawing over other applications, including games, without
/// interfering with them.
/// </summary>
/// <remarks>
/// It stays above other windows, passes mouse input through, never takes focus, has no taskbar or
/// Alt+Tab entry, and is left out of screen capture so it never appears in clips. It has no
/// redirection bitmap: its content comes only from a DirectComposition target.
/// </remarks>
internal sealed class OverlayWindow : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private const uint LWA_ALPHA = 0x2;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private const int SW_HIDE = 0;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = -1;

    public OverlayWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Clipping overlay",
            Style = WS_POPUP,
            // LAYERED with TRANSPARENT is what makes the window click-through.
            ExStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED
                | WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE,
        });

        // A layered window shows nothing until its attributes are set. Fully opaque: the page's
        // own transparency is what lets the game show through.
        if (!SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Without this the overlay would be recorded into the next clip. Needs Windows 10 2004.
        if (!SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Shows the window at <paramref name="bounds"/>, in virtual-desktop pixels, above other
    /// windows and without activating it.
    /// </summary>
    public void ShowAt(Rectangle bounds) =>
        SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    /// <summary>Destroys the window. Call on the thread that created it.</summary>
    public void Dispose() => DestroyHandle();

    protected override void WndProc(ref Message m)
    {
        // Backs up WS_EX_NOACTIVATE: a click must never pull focus from the game.
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
}
