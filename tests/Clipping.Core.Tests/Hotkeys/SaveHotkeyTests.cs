using Clipping.Core.Hotkeys;
using Clipping.Core.Settings;
using Clipping.Core.Shell;

namespace Clipping.Core.Tests.Hotkeys;

public sealed class SaveHotkeyTests : IDisposable
{
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly FakeNotifications _notifications = new();
    private readonly string _directory;
    private readonly string _settingsPath;

    public SaveHotkeyTests()
    {
        // xUnit creates a new instance per test, so each test gets its own settings file.
        _directory = Path.Combine(Path.GetTempPath(), "Clipping.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // The combination saved last session must save clips as soon as the app starts.
    [Fact]
    public void Constructor_RegistersTheSavedCombination()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        store.Update(s => s with { SelectedHotkey = "Ctrl+Shift+S" });
        var saves = 0;

        // Act
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);
        _hotkeys.Press("Ctrl+Shift+S");

        // Assert
        Assert.Equal(1, saves);
        Assert.Empty(_notifications.Shown);
    }

    // A save key that silently does nothing would lose the user's highlight mid-game.
    [Fact]
    public void Constructor_WhenSavedCombinationIsTaken_TellsTheUserAndKeepsRunning()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        _hotkeys.TakenByAnotherApp.Add("Alt+P");

        // Act
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => { });

        // Assert
        Assert.Null(hotkey.ActiveCombination);
        var shown = Assert.Single(_notifications.Shown);
        Assert.Equal(NotificationKind.Error, shown.Kind);
        Assert.Contains("Alt+P", shown.Message);
    }

    [Fact]
    public void Rebind_SavesClipsWithTheNewCombinationAndPersistsIt()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);

        // Act
        hotkey.Rebind("Ctrl+Shift+S");
        _hotkeys.Press("Ctrl+Shift+S");

        // Assert
        Assert.Equal(1, saves);
        Assert.Equal("Ctrl+Shift+S", new JsonSettingsStore(_settingsPath).Settings.SelectedHotkey);
    }

    // The old combination goes back to Windows, so other apps can use it and it no longer saves.
    [Fact]
    public void Rebind_ReleasesTheOldCombination()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);

        // Act
        hotkey.Rebind("Ctrl+Shift+S");
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Equal(0, saves);
        Assert.Equal(["Ctrl+Shift+S"], _hotkeys.Registered);
    }

    // All or nothing: a combination another app owns changes neither the setting nor the working key.
    [Fact]
    public void Rebind_WhenCombinationIsTaken_ThrowsAndKeepsTheOldHotkeyAndSetting()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);
        _hotkeys.TakenByAnotherApp.Add("Ctrl+Shift+S");

        // Act
        var error = Assert.Throws<HotkeyUnavailableException>(() => hotkey.Rebind("Ctrl+Shift+S"));
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Equal("Ctrl+Shift+S", error.Combination);
        Assert.Equal(1, saves);
        Assert.Equal(["Alt+P"], _hotkeys.Registered);
        Assert.Equal("Alt+P", new JsonSettingsStore(_settingsPath).Settings.SelectedHotkey);
    }

    // All or nothing: if the setting can't be saved, the new combination is released again.
    [Fact]
    public void Rebind_WhenSavingFails_ThrowsAndKeepsTheOldHotkey()
    {
        // Arrange
        var store = new FailingSettingsStore();
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);

        // Act
        Assert.Throws<IOException>(() => hotkey.Rebind("Ctrl+Shift+S"));
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Equal(1, saves);
        Assert.Equal(["Alt+P"], _hotkeys.Registered);
        Assert.Equal("Alt+P", hotkey.ActiveCombination);
    }

    [Fact]
    public void Rebind_WhenNotificationThrows_KeepsTheNewHotkeyAndPersistedSettingTogether()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);
        var failure = new InvalidOperationException("Notification failed.");
        store.Changed += (_, _) => throw failure;

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => hotkey.Rebind("Ctrl+Shift+S"));
        _hotkeys.Press("Alt+P");
        _hotkeys.Press("Ctrl+Shift+S");

        // Assert
        Assert.Same(failure, error);
        Assert.Equal(1, saves);
        Assert.Equal(["Ctrl+Shift+S"], _hotkeys.Registered);
        Assert.Equal("Ctrl+Shift+S", hotkey.ActiveCombination);
        Assert.Equal("Ctrl+Shift+S", store.Settings.SelectedHotkey);
        Assert.Equal("Ctrl+Shift+S", new JsonSettingsStore(_settingsPath).Settings.SelectedHotkey);
    }

    // Registering a combination we already own would be reported as "in use" by ourselves.
    [Fact]
    public void Rebind_ToTheCurrentCombinationSpelledDifferently_KeepsItWorking()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        store.Update(s => s with { SelectedHotkey = "Ctrl+Alt+P" });
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);

        // Act
        hotkey.Rebind("alt + control + p");
        _hotkeys.Press("Ctrl+Alt+P");

        // Assert
        Assert.Equal(1, saves);
        Assert.Equal(["Ctrl+Alt+P"], _hotkeys.Registered);
    }

    // Choosing a free combination is how the user recovers from a taken one at startup.
    [Fact]
    public void Rebind_AfterSavedCombinationWasTaken_RegistersTheNewOne()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        _hotkeys.TakenByAnotherApp.Add("Alt+P");
        var saves = 0;
        using var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => saves++);

        // Act
        hotkey.Rebind("Ctrl+Shift+S");
        _hotkeys.Press("Ctrl+Shift+S");

        // Assert
        Assert.Equal(1, saves);
        Assert.Equal("Ctrl+Shift+S", new JsonSettingsStore(_settingsPath).Settings.SelectedHotkey);
    }

    // The combination must go back to Windows when the app exits.
    [Fact]
    public void Dispose_ReleasesTheCombination()
    {
        // Arrange
        var store = new JsonSettingsStore(_settingsPath);
        var hotkey = new SaveHotkey(_hotkeys, store, _notifications, () => { });

        // Act
        hotkey.Dispose();

        // Assert
        Assert.Empty(_hotkeys.Registered);
    }

    // Behaves like RegisterHotKey: a combination can be owned once, by us or by another app.
    private sealed class FakeHotkeyService : IHotkeyService
    {
        private readonly List<(string Combination, Action OnPressed)> _registrations = [];

        public HashSet<string> TakenByAnotherApp { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Registered => _registrations.Select(r => r.Combination).ToList();

        public IDisposable Register(string combination, Action onPressed)
        {
            if (TakenByAnotherApp.Contains(combination)
                || _registrations.Any(r => SaveHotkey.IsSameCombination(r.Combination, combination)))
            {
                throw new HotkeyUnavailableException(combination, $"{combination} is already used by another app.");
            }

            var registration = (combination, onPressed);
            _registrations.Add(registration);
            return new Release(() => _registrations.Remove(registration));
        }

        public void Press(string combination)
        {
            foreach (var (_, onPressed) in _registrations.Where(r => SaveHotkey.IsSameCombination(r.Combination, combination)).ToList())
            {
                onPressed();
            }
        }

        private sealed class Release(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }

    // Saving failures (disk full, file locked) are hard to cause reliably with the real store.
    private sealed class FailingSettingsStore : ISettingsStore
    {
        public AppSettings Settings { get; } = new();

        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }

        public void Update(Func<AppSettings, AppSettings> change) => throw new IOException("The disk is full.");
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<(string Title, string Message, NotificationKind Kind)> Shown { get; } = [];

        public void Show(string title, string message, NotificationKind kind, Action? onClicked = null) =>
            Shown.Add((title, message, kind));
    }
}
