using Clipping.Core.Settings;
using Clipping.Core.Shell;

namespace Clipping.Core.Hotkeys;

/// <summary>
/// Keeps the clip-save hotkey registered to <see cref="AppSettings.SelectedHotkey"/>.
/// </summary>
/// <remarks>
/// On creation, registers the saved combination, or tells the user if it can't. The app keeps
/// recording either way.
/// <para>
/// Create, use, and dispose it on the thread that owns the <see cref="IHotkeyService"/>.
/// </para>
/// </remarks>
public sealed class SaveHotkey : IDisposable
{
    private readonly IHotkeyService _hotkeys;
    private readonly ISettingsStore _settings;
    private readonly Action _onPressed;

    // Only the active binding saves; a binding still being committed by Rebind stays silent.
    private Binding? _active;
    private bool _disposed;

    /// <param name="hotkeys">Registers the combination.</param>
    /// <param name="settings">Where the combination is read from and saved to.</param>
    /// <param name="notifications">Tells the user when the saved combination can't be registered.</param>
    /// <param name="onPressed">Saves a clip. Runs on the hotkey service's thread and must return quickly.</param>
    public SaveHotkey(
        IHotkeyService hotkeys, ISettingsStore settings, INotificationService notifications, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(onPressed);

        _hotkeys = hotkeys;
        _settings = settings;
        _onPressed = onPressed;

        var saved = settings.Settings.SelectedHotkey;
        try
        {
            _active = Bind(saved);
        }
        catch (HotkeyUnavailableException e)
        {
            // A missing save key is easy to miss mid-game, so say so instead of only logging.
            notifications.Show("Save hotkey unavailable", e.Message, NotificationKind.Error);
        }
    }

    /// <summary>
    /// The combination that currently saves a clip, or <see langword="null"/> if none is registered.
    /// </summary>
    public string? ActiveCombination => _active?.Combination;

    /// <summary>
    /// Switches the save hotkey to <paramref name="combination"/> and persists it.
    /// </summary>
    /// <remarks>
    /// All or nothing: the new combination is registered first and the old one stays active until
    /// the setting is saved. Registration or saving failures leave both unchanged. If a settings
    /// notification throws after saving, both use the new combination and the exception propagates.
    /// </remarks>
    /// <exception cref="HotkeyUnavailableException">The combination is invalid or already in use.</exception>
    public void Rebind(string combination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Registering a combination we already own would fail as "in use"; there is nothing to change.
        if (_active is not null && IsSameCombination(_active.Combination, combination))
        {
            return;
        }

        // 1. Register the new combination while the old one keeps working.
        var replacement = Bind(combination);

        // 2. Persist it. A Changed handler can throw after the setting has already been saved.
        try
        {
            _settings.Update(s => s with { SelectedHotkey = combination });
        }
        finally
        {
            if (_settings.Settings.SelectedHotkey == combination)
            {
                // 3. Match the committed setting, then 4. release the old combination.
                var previous = _active;
                _active = replacement;
                previous?.Registration.Dispose();
            }
            else
            {
                replacement.Registration.Dispose();
            }
        }
    }

    /// <summary>
    /// Unregisters the save hotkey.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _active?.Registration.Dispose();
        _active = null;
    }

    private Binding Bind(string combination)
    {
        var binding = new Binding(combination);
        binding.Registration = _hotkeys.Register(combination, () =>
        {
            if (ReferenceEquals(_active, binding))
            {
                _onPressed();
            }
        });

        return binding;
    }

    // "ctrl+ALT+p" and "Alt+Ctrl+P" are the same key press.
    internal static bool IsSameCombination(string a, string b) =>
        Normalize(a).SetEquals(Normalize(b));

    private static HashSet<string> Normalize(string combination) =>
        combination
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.ToUpperInvariant() switch
            {
                "CONTROL" => "CTRL",
                var upper => upper,
            })
            .ToHashSet(StringComparer.Ordinal);

    private sealed class Binding(string combination)
    {
        public string Combination { get; } = combination;

        public IDisposable Registration { get; set; } = null!;
    }
}
