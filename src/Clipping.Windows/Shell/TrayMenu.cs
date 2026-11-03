using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// Keeps a WinForms item collection laid out from the entries features added.
/// </summary>
/// <remarks>
/// Entries are created on the calling thread, but every change to the collection is posted to
/// the shell's UI thread, so <see cref="_entries"/> and the collection are only touched there.
/// </remarks>
internal sealed class TrayMenu : ITrayMenu
{
    private readonly Func<ToolStripItemCollection> _getItems;
    private readonly MessageWindow _window;
    private readonly List<TrayMenuEntry> _entries = [];
    private readonly List<ToolStripSeparator> _separators = [];
    private volatile bool _closed;

    /// <param name="getItems">
    /// The collection to lay out. Called only on the UI thread, since a submenu's collection
    /// creates its drop-down, which is a control.
    /// </param>
    public TrayMenu(Func<ToolStripItemCollection> getItems, MessageWindow window)
    {
        _getItems = getItems;
        _window = window;
    }

    /// <inheritdoc />
    public IMenuEntry Add(string label, MenuGroup group, Action onClick)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(onClick);

        var entry = new TrayMenuEntry(this, _window, label, group);
        // Posted rather than run inside WinForms' click dispatch, so a handler that pumps messages
        // (a prompt) can't have the clicked item disposed under it, and skipped if the entry was
        // removed after the click was queued.
        entry.Item.Click += (_, _) => _window.Post(() =>
        {
            if (!entry.IsRetired)
            {
                onClick();
            }
        });
        Attach(entry);
        return entry;
    }

    /// <inheritdoc />
    public ITraySubmenu AddSubmenu(string label, MenuGroup group)
    {
        ArgumentNullException.ThrowIfNull(label);

        var submenu = new TraySubmenu(this, _window, label, group);
        Attach(submenu);
        return submenu;
    }

    /// <summary>
    /// Rebuilds the collection from the entries. UI thread only.
    /// </summary>
    internal void Relayout()
    {
        if (_closed)
        {
            return;
        }

        var items = _getItems();
        items.Clear();
        foreach (var separator in _separators)
        {
            separator.Dispose();
        }

        _separators.Clear();

        foreach (var entry in MenuLayout.Arrange(_entries.Select(e => (e.Group, e, e.Visible))))
        {
            if (entry is null)
            {
                var separator = new ToolStripSeparator();
                _separators.Add(separator);
                items.Add(separator);
            }
            else
            {
                items.Add(entry.Item);
            }
        }
    }

    /// <summary>
    /// Takes an entry out, retires it and anything beneath it, and disposes it. UI thread only.
    /// </summary>
    internal void Detach(TrayMenuEntry entry)
    {
        if (_entries.Remove(entry))
        {
            entry.Retire();
            Relayout();
            entry.Item.Dispose();
        }
    }

    /// <summary>
    /// Retires every entry and ignores later additions, for a submenu that was removed.
    /// UI thread only; the caller disposes the items.
    /// </summary>
    internal void Close()
    {
        _closed = true;
        foreach (var entry in _entries)
        {
            entry.Retire();
        }

        _entries.Clear();
    }

    private void Attach(TrayMenuEntry entry) =>
        _window.Post(() =>
        {
            // The submenu holding this menu may have been removed since the entry was added.
            if (_closed)
            {
                entry.Retire();
                entry.Item.Dispose();
                return;
            }

            _entries.Add(entry);
            Relayout();
        });
}
