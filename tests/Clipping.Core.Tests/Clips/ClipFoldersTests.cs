using Clipping.Core.Clips;

namespace Clipping.Core.Tests.Clips;

public sealed class ClipFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipFoldersTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void For_NewSource_CreatesFolderNamedAfterTheApp()
    {
        // Act
        var folder = ClipFolders.For(_root, new ClipSource("Cyberpunk2077.exe", "Cyberpunk2077.exe", "Cyberpunk 2077"));

        // Assert
        Assert.Equal(Path.Combine(_root, "Cyberpunk 2077"), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void For_SameAppAgain_ReturnsSameFolder()
    {
        // Arrange
        var first = ClipFolders.For(_root, new ClipSource("Cyberpunk2077.exe", "Cyberpunk2077.exe", "Cyberpunk 2077"));

        // Act
        var second = ClipFolders.For(_root, new ClipSource("CYBERPUNK2077.EXE", "CYBERPUNK2077.EXE", "Cyberpunk 2077"));

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void For_DifferentAppsWithSameName_GetSeparateFolders()
    {
        // Arrange
        var first = ClipFolders.For(_root, new ClipSource("fooclient.exe", "fooclient.exe", "Launcher"));

        // Act
        var second = ClipFolders.For(_root, new ClipSource("barclient.exe", "barclient.exe", "Launcher"));
        var firstAgain = ClipFolders.For(_root, new ClipSource("fooclient.exe", "fooclient.exe", "Launcher"));

        // Assert
        Assert.Equal(Path.Combine(_root, "Launcher"), first);
        Assert.Equal(Path.Combine(_root, "Launcher (barclient)"), second);
        Assert.Equal(first, firstAgain);
    }

    [Fact]
    public void For_OneAppWithTwoExecutables_SharesOneFolder()
    {
        // Arrange
        var first = ClipFolders.For(_root, new ClipSource("Larian|Baldur's Gate 3", "bg3.exe", "Baldur's Gate 3"));

        // Act
        var second = ClipFolders.For(_root, new ClipSource("Larian|Baldur's Gate 3", "bg3_dx11.exe", "Baldur's Gate 3"));

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void For_FileWithTheAppsName_UsesNextName()
    {
        // Arrange
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Launcher"), "");

        // Act
        var folder = ClipFolders.For(_root, new ClipSource("fooclient.exe", "fooclient.exe", "Launcher"));

        // Assert
        Assert.Equal(Path.Combine(_root, "Launcher (fooclient)"), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void For_AppWhoseNameChanged_ReusesItsFolder()
    {
        // Arrange: a program with no version info is named from a window title that differs between runs.
        var first = ClipFolders.For(_root, new ClipSource("session.exe", "session.exe", "Session A"));

        // Act
        var second = ClipFolders.For(_root, new ClipSource("session.exe", "session.exe", "Session B"));

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void For_FolderRenamedByUser_KeepsUsingIt()
    {
        // Arrange
        var original = ClipFolders.For(_root, new ClipSource("Cyberpunk2077.exe", "Cyberpunk2077.exe", "Cyberpunk 2077"));
        var renamed = Path.Combine(_root, "CP77 clips");
        Directory.Move(original, renamed);

        // Act
        var folder = ClipFolders.For(_root, new ClipSource("Cyberpunk2077.exe", "Cyberpunk2077.exe", "Cyberpunk 2077"));

        // Assert
        Assert.Equal(renamed, folder);
    }

    [Fact]
    public void For_UnknownSource_UsesUnknownFolder()
    {
        // Act
        var folder = ClipFolders.For(_root, null);

        // Assert
        Assert.Equal(Path.Combine(_root, ClipFolders.Unknown), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void For_ExistingFolderMadeByUser_IsReused()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_root, "Cyberpunk 2077"));

        // Act
        var folder = ClipFolders.For(_root, new ClipSource("Cyberpunk2077.exe", "Cyberpunk2077.exe", "Cyberpunk 2077"));

        // Assert
        Assert.Equal(Path.Combine(_root, "Cyberpunk 2077"), folder);
    }

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData(@"C:\Windows")]
    public void For_NameThatLooksLikeAPath_StaysDirectlyInsideRoot(string name)
    {
        // Act
        var folder = ClipFolders.For(_root, new ClipSource("game.exe", "game.exe", name));

        // Assert
        Assert.Equal(Path.GetFullPath(_root), Path.GetDirectoryName(Path.GetFullPath(folder)));
    }

    [Theory]
    [InlineData("Assassin's Creed: Valhalla", "Assassin's Creed Valhalla")]
    [InlineData("What? <Game> \"Edition\" | 2*", "What Game Edition 2")]
    [InlineData("Game\tWith\nControl", "Game With Control")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("CON", "CON_")]
    [InlineData("nul.txt", "nul_.txt")]
    [InlineData("COM¹", "COM¹_")]
    [InlineData("CONOUT$", "CONOUT$_")]
    public void For_NameWithCharactersWindowsRejects_UsesCleanedName(string name, string expected)
    {
        // Act
        var folder = ClipFolders.For(_root, new ClipSource("game.exe", "game.exe", name));

        // Assert
        Assert.Equal(Path.Combine(_root, expected), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void For_NameWithNothingUsable_FallsBackToExecutableName()
    {
        // Act
        var folder = ClipFolders.For(_root, new ClipSource("game.exe", "game.exe", " ?? "));

        // Assert
        Assert.Equal(Path.Combine(_root, "game"), folder);
    }

    [Fact]
    public void For_LongNameEndingInAnEmoji_DoesNotSplitTheEmoji()
    {
        // Arrange: the emoji's two halves straddle the length limit.
        var name = new string('a', 79) + "🎮";

        // Act
        var folder = ClipFolders.For(_root, new ClipSource("game.exe", "game.exe", name));

        // Assert
        Assert.Equal(Path.Combine(_root, new string('a', 79)), folder);
    }
}
