namespace Clipping.Core.Clips;

/// <summary>
/// Finds which application is on the captured monitor right now.
/// </summary>
public interface IClipSourceProbe
{
    /// <summary>
    /// The application occupying the monitor being captured.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> if no application can be identified, such as when the desktop is showing.
    /// </returns>
    /// <remarks>Called from a background thread about once a second, so it should be quick.</remarks>
    ClipSource? Current();
}
