using Clipping.Core.Shell;
using Clipping.Windows.Shell;

namespace Clipping.Core.Tests.Windows;

public sealed class MenuLayoutTests
{
    // Features register in whatever order Program.cs happens to wire them; the menu must not depend on it.
    [Fact]
    public void Arrange_OrdersByGroupAndKeepsAddOrderWithinAGroup()
    {
        // Arrange
        var entries = new[]
        {
            (MenuGroup.App, "Open log folder", true),
            (MenuGroup.Capture, "Monitor", true),
            (MenuGroup.Clips, "Clip folder", true),
            (MenuGroup.Capture, "Pause capture", true),
        };

        // Act
        var arranged = MenuLayout.Arrange(entries);

        // Assert
        Assert.Equal(
            ["Monitor", "Pause capture", null, "Clip folder", null, "Open log folder"],
            arranged);
    }

    // A separator belongs between groups only: never first, last, or inside one group.
    [Fact]
    public void Arrange_SingleGroup_HasNoSeparators()
    {
        // Arrange
        var entries = new[]
        {
            (MenuGroup.Hotkeys, "Save hotkey: Alt+P", true),
            (MenuGroup.Hotkeys, "Change hotkey", true),
        };

        // Act
        var arranged = MenuLayout.Arrange(entries);

        // Assert
        Assert.Equal(["Save hotkey: Alt+P", "Change hotkey"], arranged);
    }

    // A group whose entries are all hidden must not leave two separators side by side.
    [Fact]
    public void Arrange_HiddenEntries_AreLeftOutAndLeaveNoEmptyGroup()
    {
        // Arrange
        var entries = new[]
        {
            (MenuGroup.Capture, "Monitor", true),
            (MenuGroup.Hotkeys, "Save hotkey: Alt+P", false),
            (MenuGroup.Clips, "Clip folder", true),
        };

        // Act
        var arranged = MenuLayout.Arrange(entries);

        // Assert
        Assert.Equal(["Monitor", null, "Clip folder"], arranged);
    }
}
