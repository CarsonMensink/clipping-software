using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// A menu entry whose drop-down holds a menu of its own.
/// </summary>
internal sealed class TraySubmenu : TrayMenuEntry, ITraySubmenu
{
    private readonly TrayMenu _items;

    public TraySubmenu(TrayMenu owner, MessageWindow window, string label, MenuGroup group)
        : base(owner, window, label, group)
    {
        // DropDownItems creates the drop-down, a control, so it is only read on the UI thread.
        _items = new TrayMenu(() => Item.DropDownItems, window);
    }

    /// <inheritdoc />
    public IMenuEntry Add(string label, MenuGroup group, Action onClick) => _items.Add(label, group, onClick);

    /// <inheritdoc />
    public ITraySubmenu AddSubmenu(string label, MenuGroup group) => _items.AddSubmenu(label, group);

    /// <summary>
    /// Retires this entry and everything in its menu, at any depth.
    /// </summary>
    internal override void Retire()
    {
        base.Retire();
        _items.Close();
    }
}
