namespace Clipping.Core.Hotkeys;

/// <summary>
/// Asks the user to press a new key combination.
/// </summary>
public interface IHotkeyPrompt
{
    /// <summary>
    /// Shows the prompt and returns once it has closed.
    /// </summary>
    /// <param name="apply">
    /// Called with each combination the user settles on, written as modifiers then one key, such
    /// as <c>Ctrl+Shift+S</c>. Returns <see langword="null"/> if it was accepted, which closes
    /// the prompt, or a message for the user, who can then try another. Never called if the
    /// user cancels.
    /// </param>
    void Show(Func<string, string?> apply);
}
