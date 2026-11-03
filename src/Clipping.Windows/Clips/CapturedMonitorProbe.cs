using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Clipping.Core.Clips;
using Clipping.Core.Display;

namespace Clipping.Windows.Clips;

/// <summary>
/// Finds the application occupying the captured monitor, which isn't always the foreground one.
/// </summary>
/// <remarks>
/// Windows has one foreground window for the whole desktop, so with a game on each display the
/// captured one is often not in focus. Instead, top-level windows are listed front to back and
/// <see cref="WindowPicker"/> chooses among them by how much of the monitor each visibly covers.
/// <para>
/// Skipped: minimised and hidden windows, cloaked ones (on another virtual desktop, or suspended
/// Store apps, which report themselves visible), click-through and tool windows such as game
/// overlays, the desktop, taskbar, Alt+Tab switcher and Game Bar, and this app's own windows.
/// </para>
/// <para>
/// Store apps run inside <c>ApplicationFrameHost.exe</c>; the app is found from the hosted window
/// inside its frame. Applications are named and identified from their version information, falling
/// back to the window title and then the executable name for programs that have none. A name is
/// fixed the first time a program is seen, so a title that changes doesn't scatter its clips.
/// </para>
/// </remarks>
internal sealed class CapturedMonitorProbe(IDisplayTarget display) : IClipSourceProbe
{
    private const string FrameHostClass = "ApplicationFrameWindow";

    private static readonly HashSet<string> ShellClasses = new(
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"],
        StringComparer.Ordinal);

    // Windows 11's Alt+Tab switcher and similar shell overlays, which can cover the whole screen.
    private const string ShellOverlayClassPrefix = "XamlExplorerHostIslandWindow";

    private static readonly HashSet<string> ExcludedExecutables = new(
        ["GameBar.exe", "GameBarFTServer.exe"],
        StringComparer.OrdinalIgnoreCase);

    // Reading version resources touches the disk, so do it once per program.
    private readonly ConcurrentDictionary<string, (string Id, string Name)> _identities = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public ClipSource? Current()
    {
        var monitor = display.GetSelectedMonitor().Bounds;
        var images = new Dictionary<int, string?>();

        var windows = new List<(nint Window, Rectangle Bounds)>();
        EnumWindows((window, _) =>
        {
            if (IsCandidate(window, images) && Bounds(window) is { } bounds && bounds.IntersectsWith(monitor))
            {
                windows.Add((window, bounds));
            }

            return true;
        }, 0);

        var chosen = WindowPicker.Pick(windows, monitor);
        return chosen == 0 ? null : Describe(chosen, images);
    }

    private static bool IsCandidate(nint window, Dictionary<int, string?> images)
    {
        if (!IsWindowVisible(window) || IsIconic(window))
        {
            return false;
        }

        if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        var exStyle = GetWindowLongW(window, GWL_EXSTYLE);
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0)
        {
            return false;
        }

        var className = ClassName(window);
        if (ShellClasses.Contains(className) || className.StartsWith(ShellOverlayClassPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        var image = Image(processId, images);
        return image is null || !ExcludedExecutables.Contains(Path.GetFileName(image));
    }

    private static Rectangle? Bounds(nint window)
    {
        // The extended frame leaves out the invisible resize borders that GetWindowRect includes.
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(window, out rect))
        {
            return null;
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private ClipSource? Describe(nint window, Dictionary<int, string?> images)
    {
        GetWindowThreadProcessId(window, out var processId);
        var title = Title(window);

        // A Store app's frame belongs to the host; the app itself owns the window inside it. While
        // the app is launching or resuming the frame can be empty, and the host says nothing useful.
        if (ClassName(window) == FrameHostClass)
        {
            if (HostedProcess(window, processId) is not { } hosted)
            {
                return null;
            }

            processId = hosted;
        }

        if (Image(processId, images) is { } path)
        {
            var (id, name) = _identities.GetOrAdd(path, Identify, title);
            return new ClipSource(id, Path.GetFileName(path), name);
        }

        // Some anti-cheat drivers refuse even a limited handle; the process name needs none.
        if (ProcessName(processId) is { } processName)
        {
            var executable = processName + ".exe";
            var (_, name) = _identities.GetOrAdd(executable, static (exe, t) => (exe, t ?? Path.GetFileNameWithoutExtension(exe)), title);
            return new ClipSource(executable, executable, name);
        }

        return null;
    }

    /// <summary>
    /// Names a program from its version information, such as <c>Cyberpunk 2077</c>, falling back
    /// to its window title and then its executable name.
    /// </summary>
    /// <remarks>
    /// Identified by publisher and name when it has version information, so one game's several
    /// executables are one application; otherwise by executable name.
    /// </remarks>
    private static (string Id, string Name) Identify(string path, string? title)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);

            // Windows' own programs all share the product name "Microsoft® Windows® Operating System".
            foreach (var name in new[] { version.ProductName, version.FileDescription })
            {
                if (!string.IsNullOrWhiteSpace(name) && !name.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
                {
                    return ($"{version.CompanyName?.Trim()}|{name.Trim()}", name.Trim());
                }
            }
        }
        catch (FileNotFoundException)
        {
            // Fall through to the title.
        }

        return (Path.GetFileName(path), title ?? Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// The process behind a Store app's frame: the one owning a child window that the host doesn't.
    /// </summary>
    private static int? HostedProcess(nint frame, int hostProcessId)
    {
        int? hosted = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childProcessId);
            if (childProcessId != hostProcessId)
            {
                hosted = childProcessId;
                return false;
            }

            return true;
        }, 0);

        return hosted;
    }

    private static string? Image(int processId, Dictionary<int, string?> images)
    {
        if (!images.TryGetValue(processId, out var image))
        {
            image = ImagePath(processId);
            images[processId] = image;
        }

        return image;
    }

    private static string? ImagePath(int processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            // Long paths need a bigger buffer; the API reports failure rather than the size needed.
            for (var capacity = 512; capacity <= 32_768; capacity *= 4)
            {
                var buffer = new char[capacity];
                var length = buffer.Length;
                if (QueryFullProcessImageNameW(process, 0, buffer, ref length))
                {
                    return new string(buffer, 0, length);
                }

                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                {
                    return null;
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // The process has exited.
            return null;
        }
    }

    private static string ClassName(nint window)
    {
        Span<char> buffer = stackalloc char[256];
        var length = GetClassNameW(window, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        return length > 0 ? new string(buffer[..length]) : "";
    }

    private static string? Title(nint window)
    {
        Span<char> buffer = stackalloc char[256];
        var length = GetWindowTextW(window, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        var title = length > 0 ? new string(buffer[..length]).Trim() : "";
        return title.Length > 0 ? title : null;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    // The extended style fits in 32 bits, so this works in 32- and 64-bit processes alike.
    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, ref char className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, ref char text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out RECT value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint process, int flags, char[] path, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
