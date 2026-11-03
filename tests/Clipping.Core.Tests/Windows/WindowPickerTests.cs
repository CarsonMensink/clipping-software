using System.Drawing;
using Clipping.Windows.Clips;

namespace Clipping.Core.Tests.Windows;

public sealed class WindowPickerTests
{
    private static readonly Rectangle Monitor = new(0, 0, 2560, 1440);

    // Reported on a real desktop: a Discord window in front of a full-screen game (#15).
    [Fact]
    public void Pick_SmallWindowInFrontOfFullScreenGame_PicksTheSmallWindow()
    {
        // Arrange: Discord covers 26% of the monitor.
        var windows = new List<(nint, Rectangle)>
        {
            (1, Rectangle.FromLTRB(751, 282, 2027, 1030)),
            (2, Monitor),
        };

        // Act
        var chosen = WindowPicker.Pick(windows, Monitor);

        // Assert
        Assert.Equal(1, chosen);
    }

    [Fact]
    public void Pick_WindowedGameInFrontOfMaximisedBrowser_PicksTheGame()
    {
        // Arrange: a 1280x720 game covers 25% of a 1440p monitor.
        var windows = new List<(nint, Rectangle)>
        {
            (1, new Rectangle(640, 360, 1280, 720)),
            (2, Monitor),
        };

        // Act
        var chosen = WindowPicker.Pick(windows, Monitor);

        // Assert
        Assert.Equal(1, chosen);
    }

    [Fact]
    public void Pick_TinyWindowInFront_IsPassedOver()
    {
        // Arrange: 400x300 is about 3% of the monitor.
        var windows = new List<(nint, Rectangle)>
        {
            (1, new Rectangle(100, 100, 400, 300)),
            (2, Monitor),
        };

        // Act
        var chosen = WindowPicker.Pick(windows, Monitor);

        // Assert
        Assert.Equal(2, chosen);
    }

    [Fact]
    public void Pick_WindowMostlyHiddenByOneInFront_CountsOnlyItsVisiblePart()
    {
        // Arrange: window 2's outline is 12% of the monitor, but window 1 (8%) hides two thirds
        // of it, leaving 4% visible. Neither reaches 10%, so the full-screen game behind wins.
        var windows = new List<(nint, Rectangle)>
        {
            (1, new Rectangle(0, 0, 640, 460)),
            (2, new Rectangle(0, 0, 640, 690)),
            (3, Monitor),
        };

        // Act
        var chosen = WindowPicker.Pick(windows, Monitor);

        // Assert
        Assert.Equal(3, chosen);
    }

    [Fact]
    public void Pick_WindowsOnAnotherMonitor_AreIgnored()
    {
        // Arrange: the frontmost window is entirely on the monitor to the right.
        var windows = new List<(nint, Rectangle)>
        {
            (1, new Rectangle(2560, 0, 1920, 1080)),
            (2, Monitor),
        };

        // Act
        var chosen = WindowPicker.Pick(windows, Monitor);

        // Assert
        Assert.Equal(2, chosen);
    }

    [Fact]
    public void Pick_WhenNoWindowShowsTenPercent_PicksTheOneShowingMost()
    {
        // Arrange
        var windows = new List<(nint, Rectangle)>
        {
            (1, new Rectangle(0, 0, 300, 300)),
            (2, new Rectangle(1000, 0, 600, 500)),
        };

        // Act
        var chosen = WindowPicker.Pick(windows, Monitor);

        // Assert
        Assert.Equal(2, chosen);
    }

    [Fact]
    public void Pick_NoWindows_ReturnsNone()
    {
        // Act
        var chosen = WindowPicker.Pick([], Monitor);

        // Assert
        Assert.Equal(0, chosen);
    }
}
