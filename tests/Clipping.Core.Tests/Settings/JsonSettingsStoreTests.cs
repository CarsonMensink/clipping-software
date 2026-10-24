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

    [Fact]
    public void Update_PersistsAcrossInstances()
    {
        var expected = new AppSettings
        {
            SelectedMonitor = @"\\.\DISPLAY2",
            SelectedHotkey = "Ctrl+Shift+S",
            OutputDirectory = Path.Combine(_directory, "clips"),
            BufferLength = 120,
        };
        var store = new JsonSettingsStore(_filePath);

        store.Update(_ => expected);
        var reloaded = new JsonSettingsStore(_filePath);

        Assert.Equal(expected, reloaded.Settings);
    }

    [Fact]
    public void Update_AppliesChangeToCurrentSettings()
    {
        var store = new JsonSettingsStore(_filePath);

        store.Update(s => s with { BufferLength = 45 });
        store.Update(s => s with { SelectedHotkey = "F9" });

        Assert.Equal(new AppSettings { BufferLength = 45, SelectedHotkey = "F9" }, store.Settings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AppSettings.MinBufferLength - 1)]
    [InlineData(AppSettings.MaxBufferLength + 1)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Update_WhenBufferLengthIsOutOfRange_Throws(long bufferLength)
    {
        var store = new JsonSettingsStore(_filePath);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Update(s => s with { BufferLength = bufferLength }));

        Assert.Equal(new AppSettings(), store.Settings);
    }

    [Theory]
    [InlineData(AppSettings.MinBufferLength)]
    [InlineData((AppSettings.MaxBufferLength + AppSettings.MinBufferLength) /2)]
    [InlineData(AppSettings.MaxBufferLength)]
    public void Update_WhenBufferLengthIsInRange_Accepts(long bufferLength)
    {
        var store = new JsonSettingsStore(_filePath);

        store.Update(s => s with { BufferLength = bufferLength });

        Assert.Equal(bufferLength, store.Settings.BufferLength);
    }

    [Fact]
    public void Update_WhenParentDirectoryIsMissing_CreatesIt()
    {
        var nestedPath = Path.Combine(_directory, "nested", "deeper", "settings.json");
        var store = new JsonSettingsStore(nestedPath);

        store.Update(s => s with { BufferLength = AppSettings.MinBufferLength });

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Update_DoesNotLeaveTempFileBehind()
    {
        var store = new JsonSettingsStore(_filePath);

        store.Update(s => s with { BufferLength = AppSettings.MinBufferLength });

        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void Update_RaisesChangedWithPreviousAndCurrent()
    {
        var store = new JsonSettingsStore(_filePath);
        var raised = new List<SettingsChangedEventArgs>();
        store.Changed += (_, e) => raised.Add(e);

        store.Update(s => s with { BufferLength = 90 });

        var args = Assert.Single(raised);
        Assert.Equal(new AppSettings(), args.Previous);
        Assert.Equal(new AppSettings { BufferLength = 90 }, args.Current);
    }

    [Fact]
    public void Update_RaisesChangedAfterSettingsAreUpdated()
    {
        var store = new JsonSettingsStore(_filePath);
        AppSettings? seenInHandler = null;
        store.Changed += (_, _) => seenInHandler = store.Settings;

        store.Update(s => s with { BufferLength = 90 });

        Assert.Equal(90, seenInHandler?.BufferLength);
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

    [Fact]
    public void Update_WhenSaveFails_ThrowsAndLeavesStateUnchanged()
    {
        var store = new JsonSettingsStore(_filePath);
        var raised = false;
        store.Changed += (_, _) => raised = true;

        // A directory at the settings path makes the final move fail.
        Directory.CreateDirectory(_filePath);

        Assert.ThrowsAny<Exception>(() => store.Update(s => s with { BufferLength = 90 }));

        Assert.Equal(new AppSettings(), store.Settings);
        Assert.False(raised);
        Assert.False(File.Exists(_filePath + ".tmp"));
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
