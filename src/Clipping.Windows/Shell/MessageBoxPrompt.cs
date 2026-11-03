using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// Shows messages in a standard message box.
/// </summary>
internal sealed class MessageBoxPrompt : IMessagePrompt
{
    private readonly MessageWindow _window;

    public MessageBoxPrompt(MessageWindow window)
    {
        _window = window;
    }

    /// <inheritdoc />
    public Task ShowAsync(string title, string message, NotificationKind kind)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var icon = kind == NotificationKind.Error ? MessageBoxIcon.Error : MessageBoxIcon.Information;
        return _window.InvokeAsync(() => MessageBox.Show(message, title, MessageBoxButtons.OK, icon));
    }
}
