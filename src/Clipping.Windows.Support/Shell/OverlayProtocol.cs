using System.Text.Json;
using System.Text.Json.Serialization;
using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// The JSON messages exchanged with the overlay page. Keep in sync with
/// <c>src/Clipping.UI/src/bridge.ts</c>.
/// </summary>
internal static class OverlayProtocol
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the message that shows <paramref name="notification"/>, replacing any card on screen.
    /// </summary>
    public static string Notify(OverlayNotification notification) =>
        JsonSerializer.Serialize(
            new NotifyMessage(
                "notify",
                notification.Id,
                notification.Title,
                notification.Message,
                notification.Kind == NotificationKind.Error ? "error" : "info"),
            Json);

    /// <returns>The message, or <see langword="null"/> if it isn't one this host understands.</returns>
    public static PageMessage? ReadPageMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<RawPageMessage>(json, Json);
            return message?.Type switch
            {
                "ready" => new PageMessage.Ready(),
                "idle" when message.Id is { } id => new PageMessage.Idle(id),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record NotifyMessage(string Type, long Id, string Title, string Message, string Kind);

    private sealed record RawPageMessage(string? Type, long? Id);
}

/// <param name="Id">Identifies the card, so the page can say when that card has gone.</param>
internal sealed record OverlayNotification(long Id, string Title, string Message, NotificationKind Kind);

internal abstract record PageMessage
{
    private PageMessage()
    {
    }

    /// <summary>The page is listening and can be sent notifications.</summary>
    public sealed record Ready : PageMessage;

    /// <summary>The card <paramref name="Id"/> has faded out and nothing is on screen.</summary>
    public sealed record Idle(long Id) : PageMessage;
}
