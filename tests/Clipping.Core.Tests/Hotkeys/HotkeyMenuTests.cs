using Clipping.Core.Hotkeys;
using Clipping.Core.Settings;
using Clipping.Core.Shell;

namespace Clipping.Core.Tests.Hotkeys;

public sealed class HotkeyMenuTests : IDisposable
{
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly FakeNotifications _notifications = new();
    private readonly FakeMenu _tray = new();
    private readonly FakePrompt _prompt = new();
    private readonly string _directory;
    private readonly string _settingsPath;

    public HotkeyMenuTests()
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

    // The user must be able to see which keys save a clip without pressing them.
    [Fact]
    public void Constructor_ShowsTheCurrentCombinationInTheHotkeysGroup()
    {
        // Arrange
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => { });

        // Act
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);

        // Assert
        Assert.Equal([("Save hotkey: Alt+P", MenuGroup.Hotkeys)], _tray.Entries);
    }

    // If the saved combination was taken at startup, the entry is how the user picks another.
    [Fact]
    public void Constructor_WhenNoHotkeyIsRegistered_SaysSo()
    {
        // Arrange
        _hotkeys.TakenByAnotherApp.Add("Alt+P");
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => { });

        // Act
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);

        // Assert
        Assert.Equal([("Save hotkey: not set", MenuGroup.Hotkeys)], _tray.Entries);
    }

    [Fact]
    public void Click_WhenUserPressesAFreeCombination_UsesItPersistsItAndShowsIt()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        string? promptError = "not asked";
        _prompt.OnShow = apply => promptError = apply("Ctrl+Shift+S");

        // Act
        _tray.Click("Save hotkey: Alt+P");
        _hotkeys.Press("Alt+P");
        _hotkeys.Press("Ctrl+Shift+S");

        // Assert
        Assert.Null(promptError);
        Assert.Equal(1, saves);
        Assert.Equal("Ctrl+Shift+S", new JsonSettingsStore(_settingsPath).Settings.SelectedHotkey);
        Assert.Equal([("Save hotkey: Ctrl+Shift+S", MenuGroup.Hotkeys)], _tray.Entries);
    }

    // A rejected combination is reported in the prompt, and the previous one keeps working.
    [Fact]
    public void Click_WhenCombinationIsTaken_TellsThePromptAndKeepsTheOldHotkey()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        _hotkeys.TakenByAnotherApp.Add("Ctrl+Shift+S");
        string? promptError = null;
        _prompt.OnShow = apply => promptError = apply("Ctrl+Shift+S");

        // Act
        _tray.Click("Save hotkey: Alt+P");
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Contains("Ctrl+Shift+S", promptError);
        Assert.Equal(1, saves);
        Assert.Equal("Alt+P", new JsonSettingsStore(_settingsPath).Settings.SelectedHotkey);
        Assert.Equal([("Save hotkey: Alt+P", MenuGroup.Hotkeys)], _tray.Entries);
    }

    // After a rejection the prompt stays open, so the user's next attempt must still work.
    [Fact]
    public void Click_WhenUserPressesAFreeCombinationAfterATakenOne_UsesTheFreeOne()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        _hotkeys.TakenByAnotherApp.Add("Ctrl+Shift+S");
        _prompt.OnShow = apply =>
        {
            apply("Ctrl+Shift+S");
            apply("F9");
        };

        // Act
        _tray.Click("Save hotkey: Alt+P");
        _hotkeys.Press("F9");

        // Assert
        Assert.Equal(1, saves);
        Assert.Equal(["F9"], _hotkeys.Registered);
    }

    // The prompt has to read the current combination like any other, without a clip being saved.
    [Fact]
    public void Click_WhileThePromptIsOpen_TheCurrentCombinationIsReleased()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        IReadOnlyList<string>? registeredWhileOpen = null;
        _prompt.OnShow = _ =>
        {
            _hotkeys.Press("Alt+P");
            registeredWhileOpen = _hotkeys.Registered;
        };

        // Act
        _tray.Click("Save hotkey: Alt+P");

        // Assert
        Assert.Equal(0, saves);
        Assert.Empty(registeredWhileOpen!);
    }

    [Fact]
    public void Click_WhenUserCancels_TheOldHotkeyWorksAgain()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        _prompt.OnShow = _ => { };

        // Act
        _tray.Click("Save hotkey: Alt+P");
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Equal(1, saves);
        Assert.Equal([("Save hotkey: Alt+P", MenuGroup.Hotkeys)], _tray.Entries);
    }

    // Pressing the combination already in use is accepted and changes nothing.
    [Fact]
    public void Click_WhenUserPressesTheCurrentCombination_AcceptsItAndKeepsItWorking()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        string? promptError = "not asked";
        _prompt.OnShow = apply => promptError = apply("Alt+P");

        // Act
        _tray.Click("Save hotkey: Alt+P");
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Null(promptError);
        Assert.Equal(1, saves);
        Assert.Equal(["Alt+P"], _hotkeys.Registered);
    }

    // A prompt that fails must not leave the user without a save key.
    [Fact]
    public void Click_WhenThePromptThrows_TheOldHotkeyWorksAgain()
    {
        // Arrange
        var saves = 0;
        using var saveHotkey = new SaveHotkey(_hotkeys, new JsonSettingsStore(_settingsPath), _notifications, () => saves++);
        _ = new HotkeyMenu(saveHotkey, _prompt, _tray);
        _prompt.OnShow = _ => throw new InvalidOperationException("The window could not be created.");

        // Act
        Assert.Throws<InvalidOperationException>(() => _tray.Click("Save hotkey: Alt+P"));
        _hotkeys.Press("Alt+P");

        // Assert
        Assert.Equal(1, saves);
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

    // Stands in for the window: each test plays the user by calling apply, or not.
    private sealed class FakePrompt : IHotkeyPrompt
    {
        public Action<Func<string, string?>> OnShow { get; set; } = _ => { };

        public void Show(Func<string, string?> apply) => OnShow(apply);
    }

    private sealed class FakeNotifications : INotificationService
    {
        public void Show(string title, string message, NotificationKind kind)
        {
        }
    }

    // Runs clicks synchronously, as the shell does once its posted click reaches the UI thread.
    private sealed class FakeMenu : ITrayMenu
    {
        private readonly List<FakeEntry> _entries = [];

        public IReadOnlyList<(string Label, MenuGroup Group)> Entries => _entries.Select(e => (e.Label, e.Group)).ToList();

        public IMenuEntry Add(string label, MenuGroup group, Action onClick)
        {
            var entry = new FakeEntry(label, group, onClick);
            _entries.Add(entry);
            return entry;
        }

        public ITraySubmenu AddSubmenu(string label, MenuGroup group) => throw new NotSupportedException();

        public void Click(string label) => _entries.Single(e => e.Label == label).OnClick();
    }

    private sealed class FakeEntry(string label, MenuGroup group, Action onClick) : IMenuEntry
    {
        public string Label { get; set; } = label;
        public MenuGroup Group { get; } = group;
        public Action OnClick { get; } = onClick;
        public bool Enabled { get; set; } = true;
        public bool Checked { get; set; }
        public bool Visible { get; set; } = true;

        public void Remove()
        {
        }
    }
}
