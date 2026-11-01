using System.Runtime.InteropServices;
using Clipping.Core.Display;
using Windows.Graphics.Capture;

namespace Clipping.Windows.Capture;

/// <summary>
/// Creates <see cref="GraphicsCaptureItem"/>s for monitors.
/// </summary>
/// <remarks>
/// The WinRT projection has no way to create an item for a monitor, so this goes through
/// the Win32 <c>IGraphicsCaptureItemInterop</c> interface.
/// </remarks>
internal static class CaptureItemFactory
{
    /// <summary>
    /// Creates a capture item for the monitor whose GDI device name matches <paramref name="monitor"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No attached display matches the monitor.</exception>
    public static GraphicsCaptureItem CreateForMonitor(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        // Bounds are virtual-desktop pixels (the process is per-monitor DPI aware), so the
        // center always lies on that monitor while the display layout is unchanged.
        var center = new POINT
        {
            x = monitor.Bounds.Left + monitor.Bounds.Width / 2,
            y = monitor.Bounds.Top + monitor.Bounds.Height / 2,
        };
        var hmonitor = MonitorFromPoint(center, MONITOR_DEFAULTTONULL);

        // The layout may have changed since monitor was read; confirm we found the same display.
        var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        if (hmonitor == IntPtr.Zero
            || !GetMonitorInfoW(hmonitor, ref info)
            || !string.Equals(info.szDevice, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"No attached display is named '{monitor.DeviceName}' at {monitor.Bounds}.",
                nameof(monitor));
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = IGraphicsCaptureItemIid;
        var itemPointer = interop.CreateForMonitor(hmonitor, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            // FromAbi takes its own reference.
            Marshal.Release(itemPointer);
        }
    }

    private const uint MONITOR_DEFAULTTONULL = 0;

    // IID of Windows.Graphics.Capture.IGraphicsCaptureItem.
    private static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    // Declared in windows.graphics.capture.interop.h.
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    // Struct layouts mirror winuser.h.

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
