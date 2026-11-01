using System.Threading.Channels;
using Clipping.Core.Capture;

namespace Clipping.Core.Encoding;

/// <summary>
/// Compresses captured frames of one size, independent of the encoder behind it.
/// </summary>
/// <remarks>
/// Encoded frames go to the writer the encoder was created with, in timestamp order.
/// Disposing encodes any frames still inside the encoder, then releases it; it never
/// completes the writer.
/// </remarks>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Submits a frame, waiting until the encoder is ready for it.
    /// </summary>
    /// <param name="frame">
    /// A frame of the size this encoder was made for. Still owned by the caller, who may
    /// dispose it as soon as this returns.
    /// </param>
    /// <param name="timestamp">When the frame is shown in the video.</param>
    /// <exception cref="InvalidOperationException">The encoder failed and can't be used again.</exception>
    void Encode(CapturedFrame frame, TimeSpan timestamp);
}

/// <summary>
/// Creates an encoder for frames of one size.
/// </summary>
/// <param name="width">Width of the frames.</param>
/// <param name="height">Height of the frames.</param>
/// <param name="frameRate">Frames per second of the video.</param>
/// <param name="output">Where the encoder writes encoded frames.</param>
/// <exception cref="InvalidOperationException">The encoder couldn't be started.</exception>
public delegate IVideoEncoder VideoEncoderFactory(int width, int height, int frameRate, ChannelWriter<EncodedSample> output);
