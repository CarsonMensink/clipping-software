namespace Clipping.Core.Settings;

/// <summary>
/// Describes a change to the user's settings.
/// </summary>
/// <remarks>
/// Compare <see cref="Previous"/> with <see cref="Current"/> to react only to the
/// settings a handler cares about.
/// </remarks>
public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    /// <summary>
    /// The settings before the change.
    /// </summary>
    public AppSettings Previous { get; } = previous;

    /// <summary>
    /// The settings after the change.
    /// </summary>
    public AppSettings Current { get; } = current;
}
