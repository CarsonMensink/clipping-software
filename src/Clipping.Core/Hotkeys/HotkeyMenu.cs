using Clipping.Core.Shell;

namespace Clipping.Core.Hotkeys;

/// <summary>
/// Shows the save hotkey in the tray menu and lets the user change it.
/// </summary>
/// <remarks>
/// Create it on the thread that owns the <see cref="SaveHotkey"/>; menu clicks arrive there too.
/// </remarks>
public sealed class HotkeyMenu
{
    private readonly SaveHotkey _saveHotkey;
    private readonly IHotkeyPrompt _prompt;
    private readonly IMenuEntry _entry;

    // The tray menu stays clickable while the prompt is open.
    private bool _prompting;

    /// <param name="saveHotkey">The hotkey to show and rebind.</param>
    /// <param name="prompt">Captures the new combination from the user.</param>
    /// <param name="menu">The menu the entry is added to.</param>
    public HotkeyMenu(SaveHotkey saveHotkey, IHotkeyPrompt prompt, ITrayMenu menu)
    {
        ArgumentNullException.ThrowIfNull(saveHotkey);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(menu);

        _saveHotkey = saveHotkey;
        _prompt = prompt;
        _entry = menu.Add(Label(), MenuGroup.Hotkeys, PromptForHotkey);
    }

    private void PromptForHotkey()
    {
        if (_prompting)
        {
            return;
        }

        _prompting = true;
        try
        {
            // Suspended so the prompt can read the current combination without saving a clip.
            using (_saveHotkey.Suspend())
            {
                _prompt.Show(Apply);
            }
        }
        finally
        {
            _prompting = false;
            _entry.Label = Label();
        }
    }

    private string? Apply(string combination)
    {
        try
        {
            _saveHotkey.Rebind(combination);
            return null;
        }
        catch (HotkeyUnavailableException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save the hotkey: {e.Message}";
        }
    }

    private string Label() => $"Save hotkey: {_saveHotkey.ActiveCombination ?? "not set"}";
}
