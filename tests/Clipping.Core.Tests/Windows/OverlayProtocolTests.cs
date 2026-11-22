using System.Text.Json;
using Clipping.Core.Shell;
using Clipping.Windows.Shell;

namespace Clipping.Core.Tests.Windows;

// The page (src/Clipping.UI/src/bridge.ts) depends on these exact message shapes.
public sealed class OverlayProtocolTests
{
    [Fact]
    public void Notify_WritesTheMessageShapeThePageExpects()
    {
        // Arrange
        var notification = new OverlayNotification(7, "Clip not saved", "The disk is full.", NotificationKind.Error);

        // Act
        var json = OverlayProtocol.Notify(notification);

        // Assert
        var message = JsonDocument.Parse(json).RootElement;
        Assert.Equal("notify", message.GetProperty("type").GetString());
        Assert.Equal(7, message.GetProperty("id").GetInt64());
        Assert.Equal("Clip not saved", message.GetProperty("title").GetString());
        Assert.Equal("The disk is full.", message.GetProperty("message").GetString());
        Assert.Equal("error", message.GetProperty("kind").GetString());
    }

    [Fact]
    public void ReadPageMessage_Ready_IsRead()
    {
        // Act
        var message = OverlayProtocol.ReadPageMessage("""{"type":"ready"}""");

        // Assert
        Assert.IsType<PageMessage.Ready>(message);
    }

    [Fact]
    public void ReadPageMessage_Idle_CarriesTheCardId()
    {
        // Act
        var message = OverlayProtocol.ReadPageMessage("""{"type":"idle","id":7}""");

        // Assert
        var idle = Assert.IsType<PageMessage.Idle>(message);
        Assert.Equal(7, idle.Id);
    }

    // A page from a different version, or broken JSON, must not crash the shell's UI thread.
    [Theory]
    [InlineData("""{"type":"resize"}""")]
    [InlineData("""{"type":"idle"}""")]
    [InlineData("""not json""")]
    [InlineData("""null""")]
    public void ReadPageMessage_Unrecognized_ReturnsNull(string json)
    {
        // Act
        var message = OverlayProtocol.ReadPageMessage(json);

        // Assert
        Assert.Null(message);
    }
}
