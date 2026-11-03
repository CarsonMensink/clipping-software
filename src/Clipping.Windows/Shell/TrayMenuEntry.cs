using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// A feature's handle on one menu item. Setters record the value and post a refresh to the UI
/// thread, which applies whatever value is current when it runs.
/// </summary>
internal class TrayMenuEntry : IMenuEntry
{
    private readonly TrayMenu _owner;
    private readonly MessageWindow _window;

    private volatile string _label;
    private volatile bool _enabled = true;
    private volatile bool _checked;
    private volatile bool _visible = true;
    private volatile bool _retired;

    public TrayMenuEntry(TrayMenu owner, MessageWindow window, string label, MenuGroup group)
    {
        _owner = owner;
        _window = window;
        _label = label;
        Group = group;

        // A ToolStripMenuItem is a component, not a control, so it can be created off the UI thread.
        // Its drop-down is a control; see TraySubmenu.
        Item = new ToolStripMenuItem(label);
    }

    public MenuGroup Group { get; }

    /// <summary>The WinForms item. Touch only on the UI thread.</summary>
    public ToolStripMenuItem Item { get; }

    /// <summary>
    /// Whether the entry, or a submenu holding it, has been removed.
    /// </summary>
    public bool IsRetired => _retired;

    /// <inheritdoc />
    public string Label
    {
        get => _label;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _label = value;

            // Reads the field when it runs, so racing setters settle on the last value set.
            Change(() => Item.Text = _label);
        }
    }

    /// <inheritdoc />
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Change(() => Item.Enabled = _enabled);
        }
    }

    /// <inheritdoc />
    public bool Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            Change(() => Item.Checked = _checked);
        }
    }

    /// <inheritdoc />
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;

            // The layout reads Visible, so it picks up the latest value whenever it runs.
            Change(_owner.Relayout);
        }
    }

    /// <inheritdoc />
    public void Remove()
    {
        _retired = true;
        _window.Post(() => _owner.Detach(this));
    }

    /// <summary>
    /// Stops this entry from touching its item again. UI thread only.
    /// </summary>
    internal virtual void Retire() => _retired = true;

    private void Change(Action apply)
    {
        if (_retired)
        {
            return;
        }

        _window.Post(() =>
        {
            // The entry, or the submenu holding it, may have been removed since this was posted.
            if (!_retired)
            {
                apply();
            }
        });
    }
}
