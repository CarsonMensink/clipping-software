using System.Threading.Channels;
using Clipping.Core.Display;

namespace Clipping.Core.Capture;

/// <summary>
/// Continuously captures frames from the selected monitor, independent of the capture
/// API behind it.
/// </summary>
/// <remarks>
/// Disposing stops capture and releases all GPU and operating system resources.
/// </remarks>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// The captured frames, oldest first.
    /// </summary>
    /// <remarks>
    /// Each frame is a copy in a texture the source owns, so the capture API's own
    /// buffers are released immediately and never wait on the reader.
    /// <para>
    /// Bounded by the number of textures the source allocates: when the reader falls
    /// behind, the oldest unread frames are dropped and disposed to make room.
    /// </para>
    /// <para>
    /// The reader owns each frame it takes and must dispose it to return its texture.
    /// While every texture is held by the reader, new frames are dropped.
    /// </para>
    /// </remarks>
    ChannelReader<CapturedFrame> Frames { get; }

    /// <summary>
    /// Begins capturing frames from a monitor into <see cref="Frames"/>.
    /// </summary>
    /// <param name="monitor">The monitor to capture.</param>
    /// <exception cref="InvalidOperationException">Capture is already running.</exception>
    void StartCapture(MonitorInfo monitor);

    /// <summary>
    /// Stops capturing. Frames already in <see cref="Frames"/> can still be read.
    /// </summary>
    /// <remarks>
    /// Does nothing if capture is not running.
    /// </remarks>
    void StopCapture();
}
