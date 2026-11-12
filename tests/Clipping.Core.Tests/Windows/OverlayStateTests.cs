using Clipping.Core.Shell;
using Clipping.Windows.Shell;

namespace Clipping.Core.Tests.Windows;

public sealed class OverlayStateTests
{
    // A notification raised during startup, before the page has loaded, is shown once it has.
    [Fact]
    public void PageReady_RepeatedWhileCardIsVisible_StillAcceptsItsDismissal()
    {
        // Arrange
        var state = new OverlayState();
        state.Request("Clip saved", "clip.mp4", NotificationKind.Info);
        var card = state.PageReady()!;

        // Act
        var duplicate = state.PageReady();
        var canHide = state.PageIdle(card.Id);

        // Assert
        Assert.Null(duplicate);
        Assert.True(canHide);
    }

    [Fact]
    public void Request_BeforePageReady_IsShownWhenThePageBecomesReady()
    {
        // Arrange
        var state = new OverlayState();

        // Act
        var sentEarly = state.Request("Saved monitor not found", "DISPLAY2 isn't attached.", NotificationKind.Info);
        var sentOnReady = state.PageReady();

        // Assert
        Assert.Null(sentEarly);
        Assert.NotNull(sentOnReady);
        Assert.Equal("Saved monitor not found", sentOnReady.Title);
    }

    // Rapid notifications must not build a backlog of stale ones (#12).
    [Fact]
    public void Request_SeveralBeforePageReady_OnlyTheNewestIsShown()
    {
        // Arrange
        var state = new OverlayState();
        state.Request("Clip saved", "first.mp4", NotificationKind.Info);
        state.Request("Clip saved", "second.mp4", NotificationKind.Info);

        // Act
        var sent = state.PageReady();
        var sentAfter = state.PageReady();

        // Assert
        Assert.NotNull(sent);
        Assert.Equal("second.mp4", sent.Message);
        Assert.Null(sentAfter);
    }

    [Fact]
    public void Request_WhenPageReady_IsSentImmediately()
    {
        // Arrange
        var state = new OverlayState();
        state.PageReady();

        // Act
        var sent = state.Request("Clip not saved", "The disk is full.", NotificationKind.Error);

        // Assert
        Assert.NotNull(sent);
        Assert.Equal(NotificationKind.Error, sent.Kind);
    }

    [Fact]
    public void PageIdle_ForTheCardOnScreen_AllowsHidingTheWindow()
    {
        // Arrange
        var state = new OverlayState();
        state.PageReady();
        var card = state.Request("Clip saved", "clip.mp4", NotificationKind.Info)!;

        // Act
        var canHide = state.PageIdle(card.Id);

        // Assert
        Assert.True(canHide);
    }

    // The page can report the old card gone while a newer one is already on its way;
    // hiding then would swallow the newer card.
    [Fact]
    public void PageIdle_ForACardAlreadyReplaced_KeepsTheWindowShown()
    {
        // Arrange
        var state = new OverlayState();
        state.PageReady();
        var first = state.Request("Clip saved", "first.mp4", NotificationKind.Info)!;
        state.Request("Clip saved", "second.mp4", NotificationKind.Info);

        // Act
        var canHide = state.PageIdle(first.Id);

        // Assert
        Assert.False(canHide);
    }

    // After the page's process crashes, notifications wait for the reloaded page instead of being lost.
    [Fact]
    public void Request_AfterPageLost_IsHeldUntilThePageIsReadyAgain()
    {
        // Arrange
        var state = new OverlayState();
        state.PageReady();
        state.PageLost();

        // Act
        var sentWhileLost = state.Request("Clip saved", "clip.mp4", NotificationKind.Info);
        var sentOnReady = state.PageReady();

        // Assert
        Assert.Null(sentWhileLost);
        Assert.NotNull(sentOnReady);
        Assert.Equal("clip.mp4", sentOnReady.Message);
    }
}
