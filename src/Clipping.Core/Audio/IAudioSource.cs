using System.Threading.Channels;

namespace Clipping.Core.Audio;

/// <summary>
/// Continuously captures what the computer is playing, independent of the capture API behind it.
/// </summary>
/// <remarks>
/// Nothing arrives while nothing is playing: silence is a gap between packet timestamps, not a
/// stalled device. If the device fails or disappears, the source keeps retrying on its own.
/// Disposing stops capture and completes <see cref="Packets"/>.
/// </remarks>
public interface IAudioSource : IDisposable
{
    /// <summary>
    /// The captured audio, oldest first, in <see cref="AudioFormat.Default"/>.
    /// </summary>
    /// <remarks>
    /// When the reader falls behind, the oldest unread packets are dropped.
    /// </remarks>
    ChannelReader<AudioPacket> Packets { get; }

    /// <summary>
    /// Begins capturing. Does nothing if capture is already running.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops capturing. Packets already in <see cref="Packets"/> can still be read.
    /// Does nothing if capture is not running.
    /// </summary>
    void Stop();
}
