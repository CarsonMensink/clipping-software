using System.Text.Json.Nodes;
using Clipping.Core.Settings;

namespace Clipping.Core.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public JsonSettingsStoreTests()
    {
        // xUnit creates a new instance per test, so each test gets its own folder.
        _directory = Path.Combine(Path.GetTempPath(), "Clipping.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_WhenFileIsMissing_ReturnsDefaults()
    {
        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(new AppSettings(), store.Settings);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    public void Constructor_WhenFileIsCorrupt_ReturnsDefaults(string contents)
    {
        File.WriteAllText(_filePath, contents);

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(new AppSettings(), store.Settings);
    }

    [Fact]
    public void Constructor_WhenFileIsPartial_KeepsDefaultsForMissingSettings()
    {
        File.WriteAllText(_filePath, """{ "BufferLength": 60 }""");

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(new AppSettings { BufferLength = 60 }, store.Settings);
    }

    [Theory]
    [InlineData(AppSettings.MinBufferLength - 1)]
    [InlineData(AppSettings.MaxBufferLength + 1)]
    public void Constructor_WhenBufferLengthIsOutOfRange_ResetsItToDefault(long bufferLength)
    {
        File.WriteAllText(_filePath, $$"""{ "BufferLength": {{bufferLength}}, "SelectedHotkey": "F9" }""");

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(new AppSettings { SelectedHotkey = "F9" }, store.Settings);
    }

    [Theory]
    [InlineData(AppSettings.MinFrameRate - 1)]
    [InlineData(AppSettings.MaxFrameRate + 1)]
    public void Constructor_WhenFrameRateIsOutOfRange_ResetsItToDefault(int frameRate)
    {
        File.WriteAllText(_filePath, $$"""{ "FrameRate": {{frameRate}}, "SelectedHotkey": "F9" }""");

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(new AppSettings { SelectedHotkey = "F9" }, store.Settings);
    }

    [Fact]
    public void Constructor_WhenFileIsEditedByHand_DoesNotLetOutOfRangeBufferLengthThrough()
    {
        new JsonSettingsStore(_filePath).Update(s => s with { BufferLength = 60, SelectedHotkey = "F9" });

        // Simulate a user editing the saved file in a text editor.
        var json = JsonNode.Parse(File.ReadAllText(_filePath))!;
        json["BufferLength"] = 999_999;
        File.WriteAllText(_filePath, json.ToJsonString());

        var store = new JsonSettingsStore(_filePath);
        Assert.Equal(new AppSettings { SelectedHotkey = "F9" }, store.Settings);

        // The next save writes the corrected value, so the bad one doesn't come back.
        store.Update(s => s with { SelectedHotkey = "F10" });
        Assert.Equal(new AppSettings().BufferLength, new JsonSettingsStore(_filePath).Settings.BufferLength);
    }

    // Preferences must survive both first-save directory creation and later edits (#5).
    [Fact]
    public void Update_PersistsSuccessivePreferencesAcrossRestart()
    {
        // Arrange
        var nestedPath = Path.Combine(_directory, "nested", "settings.json");
        var store = new JsonSettingsStore(nestedPath);
        var expected = new AppSettings
        {
            SelectedMonitor = @"\\.\DISPLAY2",
            SelectedHotkey = "Ctrl+Shift+S",
            OutputDirectory = Path.Combine(_directory, "clips"),
            BufferLength = 120,
            FrameRate = 45,
        };

        // Act
        store.Update(_ => expected);
        store.Update(settings => settings with { SelectedHotkey = "F9" });

        // Assert
        var final = expected with { SelectedHotkey = "F9" };
        Assert.Equal(final, store.Settings);
        Assert.Equal(final, new JsonSettingsStore(nestedPath).Settings);
    }

    // Observers must receive the change only once its new preferences are usable in memory and on disk (#5).
    [Fact]
    public void Update_NotifiesConsumersWithCommittedPreviousAndCurrentSettings()
    {
        // Arrange
        var store = new JsonSettingsStore(_filePath);
        var previous = store.Settings;
        var expected = previous with { BufferLength = 90 };
        var raised = new List<(SettingsChangedEventArgs Event, AppSettings Visible, AppSettings Persisted)>();
        store.Changed += (_, args) => raised.Add((args, store.Settings, new JsonSettingsStore(_filePath).Settings));

        // Act
        store.Update(_ => expected);

        // Assert
        var notification = Assert.Single(raised);
        Assert.Equal(previous, notification.Event.Previous);
        Assert.Equal(expected, notification.Event.Current);
        Assert.Equal(expected, notification.Visible);
        Assert.Equal(expected, notification.Persisted);
    }

    [Theory]
    [InlineData(AppSettings.MinBufferLength - 1)]
    [InlineData(AppSettings.MaxBufferLength + 1)]
    public void Update_WhenBufferLengthIsOutOfRange_Throws(long bufferLength)
    {
        var store = new JsonSettingsStore(_filePath);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Update(s => s with { BufferLength = bufferLength }));

        Assert.Equal(new AppSettings(), store.Settings);
    }

    [Theory]
    [InlineData(AppSettings.MinBufferLength)]
    [InlineData(AppSettings.MaxBufferLength)]
    public void Update_WhenBufferLengthIsInRange_Accepts(long bufferLength)
    {
        var store = new JsonSettingsStore(_filePath);

        store.Update(s => s with { BufferLength = bufferLength });

        Assert.Equal(bufferLength, store.Settings.BufferLength);
    }

    [Theory]
    [InlineData(AppSettings.MinFrameRate - 1)]
    [InlineData(AppSettings.MaxFrameRate + 1)]
    public void Update_WhenFrameRateIsOutOfRange_Throws(int frameRate)
    {
        var store = new JsonSettingsStore(_filePath);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Update(s => s with { FrameRate = frameRate }));

        Assert.Equal(new AppSettings(), store.Settings);
    }

    [Theory]
    [InlineData(AppSettings.MinFrameRate)]
    [InlineData(AppSettings.MaxFrameRate)]
    public void Update_WhenFrameRateIsInRange_Accepts(int frameRate)
    {
        var store = new JsonSettingsStore(_filePath);

        store.Update(s => s with { FrameRate = frameRate });

        Assert.Equal(frameRate, store.Settings.FrameRate);
    }

    [Fact]
    public void Update_WhenChangeIsNull_Throws()
    {
        var store = new JsonSettingsStore(_filePath);

        Assert.Throws<ArgumentNullException>(() => store.Update(null!));
    }

    [Fact]
    public void Update_WhenChangeReturnsNull_ThrowsAndLeavesSettingsUnchanged()
    {
        var store = new JsonSettingsStore(_filePath);
        var raised = false;
        store.Changed += (_, _) => raised = true;

        Assert.Throws<InvalidOperationException>(() => store.Update(_ => null!));

        Assert.Equal(new AppSettings(), store.Settings);
        Assert.False(raised);
        Assert.False(File.Exists(_filePath));
    }

    // Failed persistence must not announce unapplied preferences, and a later retry can still save.
    [Fact]
    public void FailedSave_PreservesSettingsAndNotificationsUntilSuccessfulRetry()
    {
        // Arrange
        var store = new JsonSettingsStore(_filePath);
        var raised = new List<SettingsChangedEventArgs>();
        store.Changed += (_, args) => raised.Add(args);
        // A directory at the destination simulates an unavailable settings file on every host.
        Directory.CreateDirectory(_filePath);

        // Act
        var failure = Record.Exception(() => store.Update(s => s with { BufferLength = 90 }));

        // Assert
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(new AppSettings(), store.Settings);
        Assert.Empty(raised);

        // Act: the destination becomes writable again.
        Directory.Delete(_filePath);
        store.Update(s => s with { BufferLength = 90 });

        // Assert
        Assert.Equal(90, new JsonSettingsStore(_filePath).Settings.BufferLength);
        Assert.Equal(90, Assert.Single(raised).Current.BufferLength);
    }

    [Fact]
    public async Task Update_WhenCalledConcurrently_LosesNoUpdates()
    {
        var store = new JsonSettingsStore(_filePath);
        var start = store.Settings.BufferLength;
        const int updates = 50;

        await Task.WhenAll(Enumerable.Range(0, updates).Select(_ =>
            Task.Run(() => store.Update(s => s with { BufferLength = s.BufferLength + 1 }))));

        Assert.Equal(start + updates, store.Settings.BufferLength);
        Assert.Equal(start + updates, new JsonSettingsStore(_filePath).Settings.BufferLength);
    }
}
