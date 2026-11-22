using Clipping.Core.Clips;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Clips;

public sealed class ClipSourceTrackerTests : IDisposable
{
    private static readonly ClipSource GameA = new("GameA.exe", "GameA.exe", "Game A");
    private static readonly ClipSource GameB = new("GameB.exe", "GameB.exe", "Game B");
    private static readonly ClipSource Discord = new("Discord.exe", "Discord.exe", "Discord");
    private static readonly ClipSource Browser = new("Google|Chrome", "chrome.exe", "Google Chrome");
    private static readonly ClipSource FileExplorer = new("explorer.exe", "explorer.exe", "File Explorer");

    // Not a game, but not on the list of known non-games either.
    private static readonly ClipSource Editor = new("Microsoft|Visual Studio Code", "Code.exe", "Visual Studio Code");

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

    // A long buffer is filed under what was played at the end, not what filled most of it.
    [Fact]
    public void Resolve_AfterSwitchingGamesInALongBuffer_PicksTheGamePlayedLast()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 240);
        Show(GameB, seconds: 60);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameB, source);
    }

    [Fact]
    public void Resolve_AfterSwitchingGamesHalfAMinuteAgo_PicksTheNewGame()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 270);
        Show(GameB, seconds: 30);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameB, source);
    }

    [Fact]
    public void Resolve_AfterTabbingOutForTenSecondsInALongBuffer_PicksTheGame()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 290);
        Show(Discord, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_WhenRecentChecksFoundNoApp_FallsBackToEarlierOnes()
    {
        // Arrange: Game B was briefly seen long ago; Game A held the time just before the gap.
        var start = StartTracking(GameB);
        Show(GameB, seconds: 5);
        Show(GameA, seconds: 120);
        Show(null, seconds: 60);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    // A default-length buffer is a plain majority vote, as before recency weighting.
    [Fact]
    public void Resolve_WithinTheRecentWindow_CountsEverySecondTheSame()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 18);
        Show(Editor, seconds: 12);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterTabbingOutForTwentySecondsInALongBuffer_PicksTheGame()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 280);
        Show(Editor, seconds: 20);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterGoingBackToTheGameFromALongChat_PicksTheGame()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 240);
        Show(Discord, seconds: 50);
        Show(GameA, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterPlayingBetweenBrowsing_PicksTheGame()
    {
        // Arrange
        var start = StartTracking(Browser);
        Show(Browser, seconds: 240);
        Show(GameA, seconds: 50);
        Show(Browser, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterGoingBackToAnotherGame_StillCountsTheSecondGameInFull()
    {
        // Arrange: only programs known not to be games are discounted.
        var start = StartTracking(GameA);
        Show(GameA, seconds: 240);
        Show(GameB, seconds: 50);
        Show(GameA, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameB, source);
    }

    // Clipping a video in the browser, say, after a game session.
    [Fact]
    public void Resolve_WhenANonGameIsOnScreenAtTheEnd_CountsItInFull()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 270);
        Show(Browser, seconds: 30);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Browser, source);
    }

    [Fact]
    public void Resolve_AfterGlancingAwayJustBeforeSaving_StillCountsTheChatAppOrBrowserInFull()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 269);
        Show(Browser, seconds: 30);
        Show(FileExplorer, seconds: 1);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Browser, source);
    }

    // Opening the clips folder, say, before pressing the hotkey.
    [Fact]
    public void Resolve_WhenAUtilityIsOnScreenAtTheEnd_StillDiscountsIt()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 255);
        Show(FileExplorer, seconds: 45);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterChattingForMinutesAndGoingBackToTheGame_PicksTheGame()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 100);
        Show(Discord, seconds: 190);
        Show(GameA, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_WhenAChatAppIsOnScreenAtTheEnd_CountsAllOfItInFull()
    {
        // Arrange: Discord's 40 seconds count in full though a gap splits them.
        var start = StartTracking(GameA);
        Show(GameA, seconds: 255);
        Show(Discord, seconds: 30);
        Show(null, seconds: 5);
        Show(Discord, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Discord, source);
    }

    // Away from games, a browser and a chat app are weighed like any two apps.
    [Fact]
    public void Resolve_AfterABrowserVideoAndAGlanceAtChat_PicksTheBrowser()
    {
        // Arrange
        var start = StartTracking(Browser);
        Show(Browser, seconds: 290);
        Show(Discord, seconds: 10);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Browser, source);
    }

    // Die, chat, play again, check a ping, save.
    [Fact]
    public void Resolve_AfterAGlanceAtChatOnceBackInTheGame_DoesNotReviveTheEarlierChat()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 227);
        Show(Discord, seconds: 50);
        Show(GameA, seconds: 20);
        Show(Discord, seconds: 3);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterAQuickLookAtAGameDuringAStream_PicksTheStream()
    {
        // Arrange
        var start = StartTracking(Discord);
        Show(Discord, seconds: 285);
        Show(GameA, seconds: 10);
        Show(Discord, seconds: 5);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Discord, source);
    }

    // A game left long ago doesn't make a later call count as time away from it.
    [Fact]
    public void Resolve_AfterAQuickLookAtAnotherGameDuringACall_PicksTheCall()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 100);
        Show(Discord, seconds: 185);
        Show(GameB, seconds: 10);
        Show(Discord, seconds: 5);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Discord, source);
    }

    [Fact]
    public void Resolve_AfterOpeningAUtilityFollowingAChat_PicksTheChatApp()
    {
        // Arrange
        var start = StartTracking(Discord);
        Show(Discord, seconds: 270);
        Show(FileExplorer, seconds: 30);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Discord, source);
    }

    [Fact]
    public void Weight_OfTheRecentWindow_CountsEverySecondInFull()
    {
        // Arrange
        var end = TimeSpan.FromMinutes(10);

        // Act
        var weight = ClipSourceTracker.Weight(end - ClipSourceTracker.RecentWindow, end, end);

        // Assert
        Assert.Equal(ClipSourceTracker.RecentWindow.TotalSeconds, weight, precision: 9);
    }

    [Fact]
    public void Weight_OneHalfLifeBeforeTheRecentWindow_IsHalfOfAllOlderTime()
    {
        // Arrange
        var end = TimeSpan.FromMinutes(100);
        var windowStart = end - ClipSourceTracker.RecentWindow;

        // Act
        var firstHalfLife = ClipSourceTracker.Weight(windowStart - ClipSourceTracker.HalfLife, windowStart, end);
        var allOlder = ClipSourceTracker.Weight(TimeSpan.Zero, windowStart, end);

        // Assert
        Assert.Equal(allOlder / 2, firstHalfLife, precision: 9);
    }

    [Fact]
    public void Resolve_AddsUpTimeAcrossSeparateStretches()
    {
        // Arrange: the editor has the longest single stretch, Game A the most time overall.
        var start = StartTracking(GameA);
        Show(GameA, seconds: 10);
        Show(Editor, seconds: 12);
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
        // Arrange: either half alone loses to the editor; together they win.
        var dx12 = new ClipSource("Larian|Baldur's Gate 3", "bg3.exe", "Baldur's Gate 3");
        var dx11 = new ClipSource("Larian|Baldur's Gate 3", "bg3_dx11.exe", "Baldur's Gate 3");
        var start = StartTracking(dx12);
        Show(dx12, seconds: 10);
        Show(Editor, seconds: 15);
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

    [Theory]
    [InlineData(70, 25)]
    [InlineData(100, 25)]
    [InlineData(40, 20)]
    [InlineData(80, 15)]
    [InlineData(121, 25)]
    [InlineData(240, 15)]
    public void Resolve_AfterChatBetweenGamesAndAFinalGlance_PicksTheNewGame(int chatSeconds, int gameSeconds)
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 200);
        Show(Discord, seconds: chatSeconds);
        Show(GameB, seconds: gameSeconds);
        Show(Discord, seconds: 5);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameB, source);
    }

    [Fact]
    public void Resolve_AfterReturningToAGameThenOpeningAnotherApp_DoesNotReviveEarlierChat()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 200);
        Show(Discord, seconds: 50);
        Show(GameA, seconds: 20);
        Show(Editor, seconds: 6);
        Show(Discord, seconds: 4);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(GameA, source);
    }

    [Fact]
    public void Resolve_AfterTwoBriefGameVisitsSeparatedByALongStream_PicksTheStream()
    {
        // Arrange
        var start = StartTracking(GameA);
        Show(GameA, seconds: 5);
        Show(Discord, seconds: 290);
        Show(GameA, seconds: 8);
        Show(Discord, seconds: 2);

        // Act
        var source = _tracker!.Resolve(start, Now());

        // Assert
        Assert.Equal(Discord, source);
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
