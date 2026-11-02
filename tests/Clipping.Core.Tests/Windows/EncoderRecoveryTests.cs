using Clipping.Windows.Encoding;

namespace Clipping.Core.Tests.Windows;

public sealed class EncoderRecoveryTests
{
    // Startup cleanup must accept a background thread that never reached Start.
    [Fact]
    public void JoinIfStarted_UnstartedBackgroundThread_DoesNotThrow()
    {
        var thread = new Thread(() => { }) { IsBackground = true };
        Assert.True(EncoderProgress.JoinIfStarted(thread, TimeSpan.Zero));
        Assert.True((thread.ThreadState & ThreadState.Unstarted) != 0);
    }

    // A successful GetEvent can carry a failed HRESULT, or MEError with no failed HRESULT.
    [Theory]
    [InlineData(unchecked((int)0x80004005), false)]
    [InlineData(0, true)]
    public void CheckEvent_FailureIsPropagated(int status, bool isError)
    {
        Assert.ThrowsAny<Exception>(() => EncoderProgress.CheckEvent(status, isError));
    }

    // Healthy input/output events must continue to admit frames.
    [Fact]
    public void InputAndOutput_AllowTextureReuse()
    {
        using var progress = new EncoderProgress(1, TimeSpan.FromSeconds(1));
        EncoderProgress.CheckEvent(0, false);
        progress.NeedInput();
        progress.WaitForInput(default);
        progress.HaveOutput();
        progress.NeedInput();
        progress.WaitForInput(default);
    }

    // Failure wakes encoding whether it is waiting for an input event or a reusable texture.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorEvent_UnblocksBothWaits(bool exhaustSlots)
    {
        using var progress = new EncoderProgress(1, TimeSpan.FromSeconds(30));
        if (exhaustSlots)
        {
            progress.NeedInput();
            progress.WaitForInput(default);
        }

        var failure = new InvalidOperationException("Driver failed.");
        var wait = Task.Run(() => Assert.Throws<InvalidOperationException>(() => progress.WaitForInput(default)));
        progress.Fail(failure);
        var error = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(failure, error.InnerException);
    }

    // Shutdown must interrupt either wait without waiting for the driver's timeout.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_UnblocksBothWaits(bool exhaustSlots)
    {
        using var progress = new EncoderProgress(1, TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        if (exhaustSlots)
        {
            progress.NeedInput();
            progress.WaitForInput(default);
        }

        var wait = Task.Run(() => Assert.ThrowsAny<OperationCanceledException>(() => progress.WaitForInput(stop.Token)));
        stop.Cancel();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Missing driver events must become a recoverable failure rather than a permanent hang.
    [Fact]
    public void MissingEvents_TimeOut()
    {
        using var progress = new EncoderProgress(1, TimeSpan.FromMilliseconds(30));
        var failure = Assert.Throws<InvalidOperationException>(() => progress.WaitForInput(default));
        Assert.IsType<TimeoutException>(failure.InnerException);
    }
}
