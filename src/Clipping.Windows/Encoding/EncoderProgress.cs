using System.Runtime.InteropServices;

namespace Clipping.Windows.Encoding;

// Bounds hardware waits and wakes both kinds of waiter when an asynchronous encoder fails.
internal sealed class EncoderProgress(int slots, TimeSpan timeout) : IDisposable
{
    private readonly SemaphoreSlim _input = new(0);
    private readonly SemaphoreSlim _slots = new(slots);
    private readonly CancellationTokenSource _failed = new();
    private Exception? _failure;

    // A stalled driver must return control so recording can retry and shutdown can complete.
    public void WaitForInput(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _failed.Token);
        stop.CancelAfter(timeout);
        try
        {
            _slots.Wait(stop.Token);
            _input.Wait(stop.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = _failure ?? new TimeoutException("The encoder stopped requesting input or producing output.");
            Fail(failure);
            throw new InvalidOperationException("The encoder failed.", failure);
        }
    }

    // NeedInput events permit exactly one more frame submission.
    public void NeedInput() => _input.Release();

    // Output retires the oldest input texture, allowing it to be reused safely.
    public void HaveOutput() => _slots.Release();

    // Cancellation wakes waiters without inventing input credits or free texture slots.
    public void Fail(Exception failure)
    {
        Interlocked.CompareExchange(ref _failure, failure, null);
        _failed.Cancel();
    }

    // MEError can carry a successful HRESULT; event kind and status must both be checked.
    public static void CheckEvent(int status, bool isError)
    {
        Marshal.ThrowExceptionForHR(status);
        if (isError)
        {
            throw new InvalidOperationException("The encoder reported an error event.");
        }
    }

    // Background is a flag alongside Unstarted, so equality cannot determine whether Join is legal.
    public static bool JoinIfStarted(Thread thread, TimeSpan timeout)
    {
        return (thread.ThreadState & ThreadState.Unstarted) != 0 || thread.Join(timeout);
    }

    // Called after the encoder's event thread has stopped using these signals.
    public void Dispose()
    {
        _input.Dispose();
        _slots.Dispose();
        _failed.Dispose();
    }
}
