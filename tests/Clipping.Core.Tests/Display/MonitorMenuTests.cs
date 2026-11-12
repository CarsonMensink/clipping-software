using System.Drawing;
using Clipping.Core.Display;
using Clipping.Core.Settings;
using Clipping.Core.Shell;

namespace Clipping.Core.Tests.Display;

public sealed class MonitorMenuTests : IDisposable
{
    private static readonly MonitorInfo _left = new(@"\\.\DISPLAY1", "Left", new Rectangle(-1920, 0, 1920, 1080), IsPrimary: false);
    private static readonly MonitorInfo _main = new(@"\\.\DISPLAY2", "Main", new Rectangle(0, 0, 2560, 1440), IsPrimary: true);
    private static readonly MonitorInfo _right = new(@"\\.\DISPLAY3", "Right", new Rectangle(2560, 0, 1920, 1080), IsPrimary: false);

    private readonly FakeMonitorSource _source = new(_left, _main, _right);
    private readonly FakeMenu _tray = new();
    private readonly FakeNotifications _notifications = new();
    private readonly string _directory;
    private readonly string _settingsPath;

    public MonitorMenuTests()
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

    // The user must see every attached display by name, and which one is being captured.
    [Fact]
    public void Constructor_ListsMonitorsByFriendlyNameAndChecksTheSelectedOne()
    {
        // Arrange
        using var display = new DisplayTarget(_source);

        // Act
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Assert
        var submenu = Assert.Single(_tray.Submenus);
        Assert.Equal(("Monitor", MenuGroup.Capture), (submenu.Label, submenu.Group));
        Assert.Equal(["Left", "Main", "Right"], submenu.Labels);
        Assert.Equal(["Main"], submenu.CheckedLabels);
    }

    // Choosing a monitor must switch capture immediately and still apply after a restart.
    [Fact]
    public void ClickingMonitor_CapturesItAndPersistsItsDeviceName()
    {
        // Arrange
        using var display = new DisplayTarget(_source);
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Act
        _tray.Submenus[0].Click("Right");

        // Assert
        Assert.Equal(_right, display.GetSelectedMonitor());
        Assert.Equal(@"\\.\DISPLAY3", new JsonSettingsStore(_settingsPath).Settings.SelectedMonitor);
    }

    [Fact]
    public void ClickingMonitor_MovesTheCheckMarkToIt()
    {
        // Arrange
        using var display = new DisplayTarget(_source);
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Act
        _tray.Submenus[0].Click("Right");

        // Assert
        Assert.Equal(["Right"], _tray.Submenus[0].CheckedLabels);
    }

    [Fact]
    public void Constructor_RestoresMonitorSavedByAnEarlierSession()
    {
        // Arrange
        new JsonSettingsStore(_settingsPath).Update(s => s with { SelectedMonitor = @"\\.\DISPLAY3" });
        using var display = new DisplayTarget(_source);

        // Act
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Assert
        Assert.Equal(_right, display.GetSelectedMonitor());
        Assert.Equal(["Right"], _tray.Submenus[0].CheckedLabels);
    }

    // A replugged or reordered display must not be captured silently in place of the saved one.
    [Fact]
    public void Constructor_WhenSavedMonitorIsMissing_CapturesPrimaryAndTellsTheUser()
    {
        // Arrange
        new JsonSettingsStore(_settingsPath).Update(s => s with { SelectedMonitor = @"\\.\DISPLAY9" });
        using var display = new DisplayTarget(_source);

        // Act
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Assert
        Assert.Equal(_main, display.GetSelectedMonitor());
        Assert.Single(_notifications.Shown);
    }

    // Keeping the choice lets a docked laptop return to its external monitor without re-choosing.
    [Fact]
    public void Constructor_WhenSavedMonitorIsMissing_KeepsTheSavedChoice()
    {
        // Arrange
        new JsonSettingsStore(_settingsPath).Update(s => s with { SelectedMonitor = @"\\.\DISPLAY9" });
        using var display = new DisplayTarget(_source);

        // Act
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Assert
        Assert.Equal(@"\\.\DISPLAY9", new JsonSettingsStore(_settingsPath).Settings.SelectedMonitor);
    }

    [Fact]
    public void MonitorAttached_AppearsInMenu()
    {
        // Arrange
        using var display = new DisplayTarget(_source);
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);
        var added = new MonitorInfo(@"\\.\DISPLAY4", "Projector", new Rectangle(0, 1440, 1920, 1080), IsPrimary: false);

        // Act
        _source.SetMonitors(_left, _main, _right, added);

        // Assert
        Assert.Equal(["Left", "Main", "Right", "Projector"], _tray.Submenus[0].Labels);
        Assert.Equal(["Main"], _tray.Submenus[0].CheckedLabels);
    }

    // Unplugging the captured display falls back to primary; the menu must show that, not a stale entry.
    [Fact]
    public void SelectedMonitorRemoved_LeavesMenuAndChecksPrimary()
    {
        // Arrange
        using var display = new DisplayTarget(_source);
        using var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);
        _tray.Submenus[0].Click("Right");

        // Act
        _source.SetMonitors(_left, _main);

        // Assert
        Assert.Equal(["Left", "Main"], _tray.Submenus[0].Labels);
        Assert.Equal(["Main"], _tray.Submenus[0].CheckedLabels);
    }

    [Fact]
    public void Dispose_StopsFollowingDisplayChanges()
    {
        // Arrange
        using var display = new DisplayTarget(_source);
        var menu = new MonitorMenu(display, new JsonSettingsStore(_settingsPath), _tray, _notifications);

        // Act
        menu.Dispose();
        _source.SetMonitors(_left, _main);

        // Assert
        Assert.Equal(["Left", "Main", "Right"], _tray.Submenus[0].Labels);
    }

    private sealed class FakeMonitorSource(params MonitorInfo[] monitors) : IMonitorSource
    {
        private IReadOnlyList<MonitorInfo> _monitors = monitors;

        public event EventHandler? MonitorsChanged;

        public IReadOnlyList<MonitorInfo> GetMonitors() => _monitors;

        public void SetMonitors(params MonitorInfo[] monitors)
        {
            _monitors = monitors;
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<(string Title, string Message, NotificationKind Kind)> Shown { get; } = [];

        public void Show(string title, string message, NotificationKind kind) =>
            Shown.Add((title, message, kind));
    }

    // Runs clicks synchronously, as the shell does once its posted click reaches the UI thread.
    private class FakeMenu : ITrayMenu
    {
        private readonly List<FakeEntry> _entries = [];

        public List<FakeSubmenu> Submenus { get; } = [];

        public IReadOnlyList<string> Labels => Visible.Select(e => e.Label).ToList();

        public IReadOnlyList<string> CheckedLabels => Visible.Where(e => e.Checked).Select(e => e.Label).ToList();

        private IEnumerable<FakeEntry> Visible => _entries.Where(e => !e.Removed);

        public IMenuEntry Add(string label, MenuGroup group, Action onClick)
        {
            var entry = new FakeEntry(label, group, onClick);
            _entries.Add(entry);
            return entry;
        }

        public ITraySubmenu AddSubmenu(string label, MenuGroup group)
        {
            var submenu = new FakeSubmenu(label, group);
            Submenus.Add(submenu);
            return submenu;
        }

        public void Click(string label) => Visible.Single(e => e.Label == label).OnClick();
    }

    private sealed class FakeSubmenu(string label, MenuGroup group) : FakeMenu, ITraySubmenu
    {
        public string Label { get; set; } = label;
        public MenuGroup Group { get; } = group;
        public bool Enabled { get; set; } = true;
        public bool Checked { get; set; }
        public bool Visible { get; set; } = true;

        public void Remove() { }
    }

    private sealed class FakeEntry(string label, MenuGroup group, Action onClick) : IMenuEntry
    {
        public string Label { get; set; } = label;
        public MenuGroup Group { get; } = group;
        public Action OnClick { get; } = onClick;
        public bool Enabled { get; set; } = true;
        public bool Checked { get; set; }
        public bool Visible { get; set; } = true;
        public bool Removed { get; private set; }

        public void Remove() => Removed = true;
    }
}
