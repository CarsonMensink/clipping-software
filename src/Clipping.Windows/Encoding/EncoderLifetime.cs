namespace Clipping.Windows.Encoding;

// One activation object can belong to only one encoder until its event thread and cleanup finish.
internal sealed class EncoderLifetime(TimeSpan joinTimeout) : IDisposable
{
    private Session? _session;
    private bool _disposed;

    // Failed construction also reserves the activation, so retries cannot bypass unfinished teardown.
    public Session Begin(Thread thread, Action shutdown, Action release)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is { } previous && !previous.TryRelease())
        {
            throw new InvalidOperationException("Recording is paused until the previous encoder thread exits.");
        }

        return _session = new Session(thread, shutdown, release, joinTimeout);
    }

    // App shutdown keeps the shared GPU, activation and Media Foundation alive until the worker exits.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Retire(Timeout.InfiniteTimeSpan);
    }

    // Lifecycle calls belong to the encoding thread; only publication also runs on the event thread.
    internal sealed class Session(Thread thread, Action shutdown, Action release, TimeSpan joinTimeout)
    {
        private readonly Lock _publication = new();
        private volatile bool _retired;
        private bool _shutdownRequested;
        private bool _releaseAttempted;
        private Exception? _releaseFailure;

        public bool IsRetired => _retired;

        // Retirement and publication share a lock so no callback can finish after retirement returns.
        public void RunIfActive(Action publish)
        {
            lock (_publication)
            {
                if (!_retired)
                {
                    publish();
                }
            }
        }

        // Stop publication before shutdown, but never hold its lock while waiting for the worker.
        public void Retire() => Retire(joinTimeout);

        // A missed deadline retains every resource and keeps Begin closed until the thread exits.
        internal void Retire(TimeSpan timeout)
        {
            lock (_publication)
            {
                _retired = true;
            }

            Exception? shutdownFailure = null;
            if (!_shutdownRequested)
            {
                _shutdownRequested = true;
                try
                {
                    shutdown();
                }
                catch (Exception failure)
                {
                    shutdownFailure = failure;
                }
            }

            if (!EncoderProgress.JoinIfStarted(thread, timeout))
            {
                throw new InvalidOperationException(
                    "The encoder thread has not exited; its resources are retained and recording is paused.",
                    shutdownFailure);
            }

            ReleaseOnce();
            if (shutdownFailure is not null)
            {
                throw new InvalidOperationException("Couldn't shut down the encoder cleanly.", shutdownFailure);
            }
        }

        // Retry attempts may finish deferred cleanup only after the retired worker has terminated.
        internal bool TryRelease()
        {
            if (!_retired || !EncoderProgress.JoinIfStarted(thread, TimeSpan.Zero))
            {
                return false;
            }

            ReleaseOnce();
            return true;
        }

        // Failed cleanup must keep activation closed, and retries must not dispose resources twice.
        private void ReleaseOnce()
        {
            if (!_releaseAttempted)
            {
                _releaseAttempted = true;
                try
                {
                    release();
                }
                catch (Exception failure)
                {
                    _releaseFailure = failure;
                }
            }

            if (_releaseFailure is not null)
            {
                throw new InvalidOperationException("The previous encoder could not release its resources.", _releaseFailure);
            }
        }
    }
}
