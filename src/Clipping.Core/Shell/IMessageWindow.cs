namespace Clipping.Core.Shell;

/// <summary>
/// The shell's hidden window, for features that need a window handle and its messages,
/// such as <c>RegisterHotKey</c> and <c>WM_HOTKEY</c>.
/// </summary>
public interface IMessageWindow
{
    /// <summary>The window's HWND.</summary>
    nint Handle { get; }

    /// <summary>
    /// Receives every message sent to the window until the returned object is disposed.
    /// </summary>
    /// <param name="hook">
    /// Called on the window's thread with the message, its wParam, and its lParam. Returns
    /// <see langword="true"/> if it handled the message, so no later hook sees it. Must return
    /// quickly and must not wait on other threads that may be waiting on the window's thread.
    /// </param>
    /// <remarks>Callable from any thread.</remarks>
    IDisposable AddHook(Func<uint, nint, nint, bool> hook);

    /// <summary>
    /// Runs <paramref name="work"/> on the window's thread, as calls such as
    /// <c>RegisterHotKey</c> require.
    /// </summary>
    /// <returns>
    /// Completes with the result or exception of <paramref name="work"/>. Runs inline when
    /// already on the window's thread. Never completes if the shell has shut down.
    /// </returns>
    /// <remarks>
    /// Work runs in the order it was queued, but work that pumps messages, such as a modal
    /// dialog, lets later work run before it returns.
    /// </remarks>
    Task<T> InvokeAsync<T>(Func<T> work);

    /// <inheritdoc cref="InvokeAsync{T}(Func{T})"/>
    Task InvokeAsync(Action work);
}
