using Clipping.Core.Hotkeys;

namespace Clipping.Windows.Shell;

/// <summary>
/// A small window by the tray that reads the next key combination the user presses.
/// </summary>
/// <remarks>
/// A bar drains while it waits and closes the window when empty. Once a combination is pressed
/// the bar restarts, and the combination is applied when it empties; pressing another restarts
/// it. Esc, or clicking elsewhere, cancels. Call <see cref="Show"/> on the UI thread.
/// </remarks>
internal sealed class HotkeyPrompt : IHotkeyPrompt
{
    /// <inheritdoc />
    public void Show(Func<string, string?> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        using var form = new PromptForm(apply);
        form.ShowDialog();
    }

    private sealed class PromptForm : Form
    {
        private const double WaitMs = 5000;
        private const double ConfirmMs = 2000;
        private const Keys HeldModifiers = Keys.Control | Keys.Alt | Keys.Shift;

        private static readonly Color Background = Color.FromArgb(32, 32, 32);
        private static readonly Color Border = Color.FromArgb(70, 70, 70);
        private static readonly Color Bright = Color.White;
        private static readonly Color Muted = Color.FromArgb(150, 150, 150);
        private static readonly Color Accent = Color.FromArgb(76, 110, 245);
        private static readonly Color Error = Color.FromArgb(255, 107, 107);

        private readonly Func<string, string?> _apply;
        private readonly Label _combination;
        private readonly Label _status;
        private readonly Panel _track;
        private readonly Panel _bar;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Font _small = new("Segoe UI", 9f);
        private readonly Font _prompt = new("Segoe UI", 10f);
        private readonly Font _keys = new("Segoe UI", 14f, FontStyle.Bold);

        // Null while waiting; otherwise the combination that is applied when the bar empties.
        private string? _captured;
        private string? _error;
        private double _remainingMs = WaitMs;
        private long _lastTick;

        public PromptForm(Func<string, string?> apply)
        {
            _apply = apply;

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Border;
            Padding = new Padding(1);
            Font = _small;

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Background };
            var title = new Label
            {
                Text = "Set save hotkey", ForeColor = Muted, Bounds = new Rectangle(16, 12, 144, 18),
            };
            var cancelHint = new Label
            {
                Text = "Esc to cancel", ForeColor = Muted, Bounds = new Rectangle(160, 12, 144, 18),
                TextAlign = ContentAlignment.TopRight,
            };
            _combination = new Label
            {
                Bounds = new Rectangle(16, 34, 288, 40), TextAlign = ContentAlignment.MiddleCenter,
            };
            _track = new Panel { Bounds = new Rectangle(16, 80, 288, 5), BackColor = Border };
            _bar = new Panel { Bounds = new Rectangle(0, 0, 288, 5), BackColor = Accent };
            _track.Controls.Add(_bar);
            _status = new Label { ForeColor = Muted, Bounds = new Rectangle(16, 92, 288, 34) };

            body.Controls.AddRange([title, cancelHint, _combination, _track, _status]);
            Controls.Add(body);

            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(322, 136);
            ResumeLayout(performLayout: false);

            _timer = new System.Windows.Forms.Timer { Interval = 30 };
            _timer.Tick += (_, _) => Advance();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Bottom-right of the screen the tray click came from, where the menu just was.
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // A tray app has no foreground window of its own, and keys only reach the foreground.
            Activate();
            _lastTick = Environment.TickCount64;
            Render(Keys.None);
            _timer.Start();
        }

        // Clicking anywhere else cancels.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }

        // Sees every key press before WinForms acts on it, so Enter, Tab, and Alt+F4 are captured
        // like any other key instead of doing what they normally do in a dialog.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const long WasAlreadyDown = 1L << 30;

            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape)
            {
                Close();
            }
            else if (!IsModifier(key) && (msg.LParam.ToInt64() & WasAlreadyDown) == 0)
            {
                // Named as in System.Windows.Forms.Keys, which is what HotkeyService parses.
                _captured = Describe(keyData & HeldModifiers, key.ToString());
                _error = null;
                _remainingMs = ConfirmMs;
                Render(Keys.None);
            }

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _small.Dispose();
                _prompt.Dispose();
                _keys.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Advance()
        {
            var now = Environment.TickCount64;
            var elapsed = now - _lastTick;
            _lastTick = now;

            // Modifiers are polled because their release sends no key press. Holding one pauses
            // the wait, so the window doesn't close on someone reaching for the last key.
            var held = _captured is null ? ModifierKeys & HeldModifiers : Keys.None;
            if (held == Keys.None)
            {
                _remainingMs -= elapsed;
            }

            if (_remainingMs <= 0)
            {
                if (_captured is null)
                {
                    Close();
                    return;
                }

                if (!TryApply(_captured))
                {
                    return;
                }
            }

            Render(held);
        }

        // False if the window closed.
        private bool TryApply(string combination)
        {
            try
            {
                _error = _apply(combination);
            }
            catch
            {
                // Left open, the next tick would apply the same combination again.
                Close();
                throw;
            }

            if (_error is null)
            {
                Close();
                return false;
            }

            _captured = null;
            _remainingMs = WaitMs;
            return true;
        }

        private void Render(Keys held)
        {
            if (_captured is not null)
            {
                Display(_captured, Bright, large: true, "Applying…", Muted);
            }
            else if (held != Keys.None)
            {
                Display(Describe(held, key: null), Muted, large: true, "Paused", Muted);
            }
            else
            {
                Display("Press your new hotkey…", Muted, large: false, _error ?? "Waiting", _error is null ? Muted : Error);
            }

            var total = _captured is null ? WaitMs : ConfirmMs;
            _bar.Width = (int)(_track.Width * Math.Clamp(_remainingMs / total, 0, 1));
        }

        private void Display(string text, Color color, bool large, string status, Color statusColor)
        {
            _combination.Text = text;
            _combination.ForeColor = color;
            _combination.Font = large ? _keys : _prompt;
            _status.Text = status;
            _status.ForeColor = statusColor;
        }

        private static bool IsModifier(Keys key) =>
            key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin;

        private static string Describe(Keys modifiers, string? key)
        {
            var parts = new List<string>(4);
            if (modifiers.HasFlag(Keys.Control))
            {
                parts.Add("Ctrl");
            }

            if (modifiers.HasFlag(Keys.Alt))
            {
                parts.Add("Alt");
            }

            if (modifiers.HasFlag(Keys.Shift))
            {
                parts.Add("Shift");
            }

            if (key is not null)
            {
                parts.Add(key);
            }

            return string.Join("+", parts);
        }
    }
}
