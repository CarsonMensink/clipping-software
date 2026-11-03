using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// A message-only window: never shown, but has an HWND and receives messages while the
/// thread that created it runs a message loop.
/// </summary>
/// <remarks>
/// Also how the shell gets onto its UI thread: <see cref="Post"/> queues work and wakes the
/// window with a private message.
/// </remarks>
internal sealed class MessageWindow : NativeWindow, IMessageWindow, IDisposable
{
    private const int WM_APP = 0x8000;
    private const int RunQueuedMessage = WM_APP + 1;

    // HWND_MESSAGE as the parent makes the window message-only.
    private static readonly nint MessageOnlyParent = -3;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _threadId;
    private readonly Lock _lock = new();

    // Guards _hooks against concurrent AddHook calls, and Post against Dispose.
    // _hooks is replaced, never mutated, so WndProc can iterate it without the lock.
    private Func<uint, nint, nint, bool>[] _hooks = [];
    private volatile bool _disposed;

    public MessageWindow()
    {
        _threadId = Environment.CurrentManagedThreadId;
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

    /// <inheritdoc />
    public IDisposable AddHook(Func<uint, nint, nint, bool> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        lock (_lock)
        {
            _hooks = [.. _hooks, hook];
        }

        return new HookRegistration(this, hook);
    }

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (Environment.CurrentManagedThreadId == _threadId)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception failure)
            {
                return Task.FromException<T>(failure);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception failure)
            {
                completion.SetException(failure);
            }
        });

        return completion.Task;
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InvokeAsync(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>
    /// Destroys the window. Must be called on the window's thread.
    /// </summary>
    public void Dispose()
    {
        // The lock is re-entrant, so a hook that posts while the window is destroyed can't deadlock.
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

        foreach (var hook in _hooks)
        {
            if (hook((uint)m.Msg, m.WParam, m.LParam))
            {
                m.Result = 0;
                return;
            }
        }

        base.WndProc(ref m);
    }

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

    private void RemoveHook(Func<uint, nint, nint, bool> hook)
    {
        lock (_lock)
        {
            _hooks = [.. _hooks.Where(h => h != hook)];
        }
    }

    private sealed class HookRegistration(MessageWindow window, Func<uint, nint, nint, bool> hook) : IDisposable
    {
        public void Dispose() => window.RemoveHook(hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
