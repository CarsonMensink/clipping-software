namespace Clipping.Core.Shell;

/// <summary>
/// An entry a feature added to the tray menu, kept to reflect the feature's state.
/// </summary>
/// <remarks>
/// Every member is callable from any thread. Getters return the last value set, which may not
/// be on screen yet.
/// </remarks>
public interface IMenuEntry
{
    /// <summary>The text shown.</summary>
    string Label { get; set; }

    /// <summary>Whether the entry can be chosen. Defaults to <see langword="true"/>.</summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Whether the entry shows a check mark. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Not exclusive: a feature with one-of-many entries clears the others itself.
    /// </remarks>
    bool Checked { get; set; }

    /// <summary>
    /// Whether the entry is in the menu. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>A hidden entry keeps its place for when it is shown again.</remarks>
    bool Visible { get; set; }

    /// <summary>
    /// Takes the entry out of the menu for good. Later changes to it are ignored, and clicks
    /// not yet handled are dropped.
    /// </summary>
    void Remove();
}
