using Clipping.Core.Clips;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Clips;

public sealed class ClipSourceTrackerTests : IDisposable
{
    private static readonly ClipSource GameA = new("GameA.exe", "GameA.exe", "Game A");
    private static readonly ClipSource GameB = new("GameB.exe", "GameB.exe", "Game B");
    private static readonly ClipSource Discord = new("Discord.exe", "Discord.exe", "Discord");

    private readonly FakeTimeProvider _time = new();
    private readonly FakeProbe _probe = new();
    private ClipSourceTracker? _tracker;

    public void Dispose() => _tracker?.Dispose();

    // Tabbing out just before pressing the hotkey must not misfile the clip (#15).
    [Fact]
    public void Resolve_AfterTabbingOutAtTheEnd_PicksAppOnScreenForMostOfTheClip()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 25);
        Show(Discord, seconds: 3);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AddsUpTimeAcrossSeparateStretches()
    {
        // Arrange: Discord has the longest single stretch, Game A the most time overall.
        var start = StartTracking(GameA);
        Show(GameA, seconds: 10);
        Show(Discord, seconds: 12);
        Show(GameA, seconds: 8);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_OnlyCountsTimeWithinTheClip()
    {
        // Arrange: Game A dominates overall, but the clip only covers Game B.
        StartTracking(GameA);
        Show(GameA, seconds: 60);
        var clipStart = Now();
        Show(GameB, seconds: 20);

        // Act
        var source = _tracker!.Resolve(clipStart, Now());

        // Assert
        Assert.Equal(GameB, source);
    }

    [Fact]
    public void Resolve_OneAppWithTwoExecutables_CountsAsOneApp()
    {
        // Arrange: either half alone loses to Discord; together they win.
        var dx12 = new ClipSource("Larian|Baldur's Gate 3", "bg3.exe", "Baldur's Gate 3");
        var dx11 = new ClipSource("Larian|Baldur's Gate 3", "bg3_dx11.exe", "Baldur's Gate 3");
        var start = StartTracking(dx12);
        Show(dx12, seconds: 10);
        Show(Discord, seconds: 15);
        Show(dx11, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal("Larian|Baldur's Gate 3", source?.Id);
    }

    [Fact]
    public void Resolve_DifferentAppsSharingAnExecutableName_CountsThemSeparately()
    {
        // Arrange
        var first = new ClipSource("Studio A|Game A", "game.exe", "Game A");
        var second = new ClipSource("Studio B|Game B", "game.exe", "Game B");
        var start = StartTracking(first);
        Show(first, seconds: 10);
        Show(second, seconds: 8);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(first, source);
    }

    [Fact]
    public void Resolve_ClipStartingBeforeTrackingBegan_CountsOnlyTrackedTime()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 5);

        // Act
        var source = _tracker!.Resolve(start - TimeSpan.FromSeconds(30), Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_IgnoresChecksThatFoundNoApp()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 3);
        Show(null, seconds: 20);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_WhenNoAppWasIdentified_ReturnsNull()
    {
        // Arrange
        var start = StartTracking(null);
        Show(null, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Null(source);
    }

    [Fact]
    public void Resolve_AfterProbeFails_KeepsTracking()
    {
        // Arrange
        var start = StartTracking(GameA);
        _probe.Failure = new InvalidOperationException("Access is denied.");
        _time.Advance(TimeSpan.FromSeconds(5));
        _probe.Failure = null;
        Show(GameB, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameB, source);
    }

    private TimeSpan StartTracking(ClipSource? source)
    {
        _probe.Source = source;
        var start = Now();
        _tracker = new ClipSourceTracker(_probe, _time);
        return start;
    }

    private void Show(ClipSource? source, int seconds)
    {
        _probe.Source = source;
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(ClipSourceTracker.SampleInterval);
        }
    }

    // The clock clip timestamps are on.
    private TimeSpan Now() => _time.GetElapsedTime(0, _time.GetTimestamp());

    private sealed class FakeProbe : IClipSourceProbe
    {
        public ClipSource? Source { get; set; }

        public Exception? Failure { get; set; }

        public ClipSource? Current() => Failure is null ? Source : throw Failure;
    }
}
