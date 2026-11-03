namespace Clipping.Core.Shell;

/// <summary>
/// A menu entry that opens a submenu of its own entries.
/// </summary>
/// <remarks>Removing it removes its entries too.</remarks>
public interface ITraySubmenu : IMenuEntry, ITrayMenu;
