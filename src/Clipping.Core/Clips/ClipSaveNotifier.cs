using Clipping.Core.Shell;

namespace Clipping.Core.Clips;

/// <summary>
/// Tells the user how a save went: which file was written, or why nothing was.
/// </summary>
public sealed class ClipSaveNotifier
{
    private readonly INotificationService _notifications;

    public ClipSaveNotifier(INotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        _notifications = notifications;
    }

    /// <summary>
    /// Shows the outcome of a save, replacing any earlier notification still on screen.
    /// </summary>
    public void Notify(ClipSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Succeeded)
        {
            _notifications.Show("Clip saved", Describe(result.Path!, result.Duration), NotificationKind.Info);
        }
        else
        {
            _notifications.Show("Clip not saved", result.Error!, NotificationKind.Error);
        }
    }

    // The application subfolder and file name, such as "Valorant\2026-11-11 21-04-33.mp4 (30.0 s)".
    // The full path would mostly repeat the output folder, which the user chose.
    private static string Describe(string path, TimeSpan duration)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        var file = Path.GetFileName(path);
        var name = string.IsNullOrEmpty(folder) ? file : Path.Combine(folder, file);
        return $"{name} ({duration.TotalSeconds:0.0} s)";
    }
}
