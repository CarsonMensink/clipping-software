using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Clipping.Windows.Shell;

/// <summary>
/// A message-only window: never shown, but has an HWND and receives messages while the
/// thread that created it runs a message loop.
/// </summary>
/// <remarks>
/// Also how the shell gets onto its UI thread: <see cref="Post"/> queues work and wakes the
/// window with a private message.
/// </remarks>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int WM_APP = 0x8000;
    private const int RunQueuedMessage = WM_APP + 1;

    // HWND_MESSAGE as the parent makes the window message-only.
    private static readonly nint MessageOnlyParent = -3;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Lock _lock = new();

    private volatile bool _disposed;

    public MessageWindow()
    {
        CreateHandle(new CreateParams { Parent = MessageOnlyParent });
    }

    /// <summary>
    /// Queues <paramref name="work"/> to run on the window's thread, after work already queued.
    /// </summary>
    /// <remarks>Callable from any thread. Dropped once the window is disposed.</remarks>
    public void Post(Action work)
    {
        // Held with Dispose, so a post never reads a handle that is being destroyed.
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _queue.Enqueue(work);
            PostMessage(Handle, RunQueuedMessage, 0, 0);
        }
    }

    /// <summary>
    /// Destroys the window. Must be called on the window's thread.
    /// </summary>
    public void Dispose()
    {
        // Posts cannot race with destruction of the native window handle.
        lock (_lock)
        {
            _disposed = true;
            DestroyHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == RunQueuedMessage)
        {
            RunQueued();
            return;
        }

        base.WndProc(ref m);
    }

    // A throwing UI action must not prevent later queued actions from being delivered.
    private void RunQueued()
    {
        try
        {
            while (_queue.TryDequeue(out var work))
            {
                work();
            }
        }
        finally
        {
            // A throwing item leaves the rest queued; wake again so they still run.
            if (!_queue.IsEmpty && !_disposed)
            {
                PostMessage(Handle, RunQueuedMessage, 0, 0);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
