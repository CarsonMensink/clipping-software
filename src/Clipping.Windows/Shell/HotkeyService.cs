using System.Runtime.InteropServices;
using Clipping.Core.Hotkeys;

namespace Clipping.Windows.Shell;

/// <summary>
/// Registers system-wide hotkeys with <c>RegisterHotKey</c> on a hidden window.
/// </summary>
/// <remarks>
/// Create, use, and dispose it on the thread that runs the message loop; hotkey presses are
/// delivered to that thread. Chosen over a low-level keyboard hook because it does not trip
/// anti-cheat heuristics.
/// </remarks>
internal sealed class HotkeyService : NativeWindow, IHotkeyService, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    // Applications may use IDs 0x0000 through 0xBFFF.
    private const int MaxId = 0xBFFF;

    private readonly Dictionary<int, Action> _handlers = [];
    private int _lastId;

    public HotkeyService()
    {
        CreateHandle(new CreateParams());
    }

    /// <inheritdoc />
    public IDisposable Register(string combination, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(combination);
        ArgumentNullException.ThrowIfNull(onPressed);

        if (!TryParse(combination, out var modifiers, out var key))
        {
            throw new HotkeyUnavailableException(
                combination,
                $"\"{combination}\" isn't a valid key combination. Use modifiers and one key, such as Alt+P.");
        }

        var id = NextId();
        if (!RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, (uint)key))
        {
            var error = Marshal.GetLastWin32Error();
            throw new HotkeyUnavailableException(
                combination,
                error == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? $"{combination} is already used by another app. Choose a different combination."
                    : $"Windows couldn't register {combination} (error {error}). Choose a different combination.");
        }

        _handlers[id] = onPressed;
        return new Registration(this, id);
    }

    /// <summary>
    /// Releases every remaining combination and destroys the window.
    /// </summary>
    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
        {
            Unregister(id);
        }

        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _handlers.TryGetValue((int)m.WParam, out var handler))
        {
            handler();
            return;
        }

        base.WndProc(ref m);
    }

    private void Unregister(int id)
    {
        if (_handlers.Remove(id))
        {
            UnregisterHotKey(Handle, id);
        }
    }

    // IDs are reused only after wrapping, and never while still registered.
    private int NextId()
    {
        do
        {
            _lastId = _lastId >= MaxId ? 1 : _lastId + 1;
        }
        while (_handlers.ContainsKey(_lastId));

        return _lastId;
    }

    // "Alt+P", "Ctrl+Shift+S": modifiers, then exactly one key named as in System.Windows.Forms.Keys.
    private static bool TryParse(string combination, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;

        foreach (var part in combination.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "ALT": modifiers |= MOD_ALT; break;
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "WIN": modifiers |= MOD_WIN; break;
                default:
                    // One key only; numbers would parse as raw key codes, and modifier flags aren't keys.
                    if (key != Keys.None
                        || part.Length == 0
                        || int.TryParse(part, out _)
                        || !Enum.TryParse(part, ignoreCase: true, out key)
                        || key == Keys.None
                        || (key & Keys.Modifiers) != 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return key != Keys.None;
    }

    private sealed class Registration(HotkeyService owner, int id) : IDisposable
    {
        public void Dispose() => owner.Unregister(id);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
