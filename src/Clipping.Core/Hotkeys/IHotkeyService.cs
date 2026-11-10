namespace Clipping.Core.Hotkeys;

/// <summary>
/// Registers system-wide key combinations.
/// </summary>
/// <remarks>
/// Knows nothing about settings or what a combination is for, so the mechanism behind it
/// (such as <c>RegisterHotKey</c> or a keyboard hook) can change without affecting callers.
/// <para>
/// Call it, and dispose its registrations, on the thread that created it. Handlers run on
/// that thread too.
/// </para>
/// </remarks>
public interface IHotkeyService
{
    /// <summary>
    /// Starts listening for <paramref name="combination"/>.
    /// </summary>
    /// <param name="combination">Modifiers then one key, such as <c>Alt+P</c> or <c>Ctrl+Shift+S</c>.</param>
    /// <param name="onPressed">Runs each time the combination is pressed. Must return quickly.</param>
    /// <returns>The registration. Dispose it to release the combination.</returns>
    /// <exception cref="HotkeyUnavailableException">
    /// The combination is invalid, or another application (or another registration) already owns it.
    /// </exception>
    IDisposable Register(string combination, Action onPressed);
}
