namespace Clipping.Core.Shell;

/// <summary>
/// The tray icon's menu, or one of its submenus.
/// </summary>
/// <remarks>
/// Entries are ordered by <see cref="MenuGroup"/>, with a separator between groups, and in the
/// order they were added within a group. Every member is callable from any thread; changes reach
/// the menu shortly after, in the order each thread made them.
/// </remarks>
public interface ITrayMenu
{
    /// <summary>
    /// Adds an entry that runs <paramref name="onClick"/> when chosen.
    /// </summary>
    /// <param name="onClick">
    /// Runs on the UI thread just after the click. Skipped if the entry was removed first;
    /// removing it cannot stop a handler that is already running.
    /// </param>
    /// <returns>The entry, for changing or removing it later.</returns>
    IMenuEntry Add(string label, MenuGroup group, Action onClick);

    /// <summary>
    /// Adds an entry that opens a submenu.
    /// </summary>
    /// <returns>The entry, which is also the submenu to add entries to.</returns>
    ITraySubmenu AddSubmenu(string label, MenuGroup group);
}
