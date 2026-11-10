using Microsoft.Extensions.Logging;

namespace Clipping.Windows.Shell;

// TEMP: a bare hotkey for testing saves, read once at startup. Remove once #10 adds the hotkey service.
internal sealed class TempSaveHotkey : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Func<Task> _onPressed;

    // Registration failure is reported in the log so recording can still run without a save key.
    public TempSaveHotkey(string combination, ILogger log, Func<Task> onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new CreateParams());

        // "Alt+P", "Ctrl+Shift+S": modifiers, then a key named as in System.Windows.Forms.Keys.
        var modifiers = MOD_NOREPEAT;
        var key = Keys.None;
        foreach (var part in combination.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "ALT": modifiers |= MOD_ALT; break;
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "WIN": modifiers |= MOD_WIN; break;
                default: Enum.TryParse(part, ignoreCase: true, out key); break;
            }
        }

        if (key == Keys.None || !RegisterHotKey(Handle, 1, modifiers, (uint)key))
        {
            log.LogWarning("TEMP: couldn't register {Combination}; it may be invalid or owned by another app.", combination);
            return;
        }

        log.LogInformation("TEMP: press {Combination} to save a clip", combination);
    }

    // Return the shortcut to Windows when this recording session ends.
    public void Dispose()
    {
        UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }

    // Saving runs asynchronously so receiving a hotkey never blocks the Windows message loop.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            _ = _onPressed();
        }

        base.WndProc(ref m);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
