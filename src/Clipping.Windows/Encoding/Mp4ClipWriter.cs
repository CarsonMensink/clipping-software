using System.Runtime.InteropServices;
using Clipping.Core.Clips;
using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Writes buffered H.264 video to an MP4 file with the Media Foundation Sink Writer.
/// </summary>
/// <remarks>
/// The samples are already encoded, so they pass straight into the file without being
/// encoded again: saving takes a fraction of a second and loses no quality. Runs on a
/// thread-pool thread, never the capture or encoding threads.
/// </remarks>
internal sealed class Mp4ClipWriter : IClipWriter
{
    /// <inheritdoc/>
    public string Extension => ".mp4";

    /// <inheritdoc/>
    public Task WriteAsync(ClipSnapshot clip, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (clip.Samples.Count == 0 || !clip.Samples[0].IsKeyframe)
        {
            throw new ArgumentException("A clip must start on a keyframe.", nameof(clip));
        }

        return Task.Run(() => WriteOrDelete(clip, path, cancellationToken), cancellationToken);
    }

    // Dispose the native writer before failed-file cleanup tries to remove its output.
    private static void WriteOrDelete(ClipSnapshot clip, string path, CancellationToken cancellationToken)
    {
        ClipFileWrite.WriteOrDelete(path, () => Write(clip, path, cancellationToken));
    }

    // Compressed samples pass through unchanged, with timestamps rebased for standalone playback.
    private static void Write(ClipSnapshot clip, string path, CancellationToken cancellationToken)
    {
        var first = clip.Samples[0];

        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        // Validate codec headers before opening a file that would need cleanup on failure.
        using var type = CreateMediaType(clip);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

        // The same type in and out tells the sink writer to pass samples through untouched.
        var stream = writer.AddStream(type);
        writer.SetInputMediaType(stream, type, null!);
        writer.BeginWriting();

        foreach (var encoded in clip.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var buffer = MediaFactory.MFCreateMemoryBuffer(encoded.Data.Length);
            buffer.Lock(out var data, out _, out _);
            try
            {
                Marshal.Copy(encoded.Data, 0, data, encoded.Data.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = encoded.Data.Length;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);

            // Rebased so the clip starts at zero.
            sample.SampleTime = (encoded.Timestamp - first.Timestamp).Ticks;
            sample.SampleDuration = encoded.Duration.Ticks;
            sample.Set(SampleAttributeKeys.CleanPoint, encoded.IsKeyframe ? 1u : 0u);
            writer.WriteSample(stream, sample);
        }

        writer.Finalize();
    }

    // The MP4 header carries retained codec parameters even when the first saved GOP omits them.
    private static IMFMediaType CreateMediaType(ClipSnapshot clip)
    {
        var first = clip.Samples[0];
        var frameRate = first.Duration > TimeSpan.Zero
            ? (int)Math.Round((double)TimeSpan.TicksPerSecond / first.Duration.Ticks)
            : 60;
        var seconds = Math.Max(clip.Duration.TotalSeconds, 0.001);
        var bitrate = (uint)Math.Min(clip.Samples.Sum(s => (long)s.Data.Length) * 8 / seconds, uint.MaxValue);

        var header = first.SequenceHeader ?? new H264ParameterSets().Update(first.Data)
            ?? throw new IOException("The clip has no complete SPS/PPS sequence header.");
        var type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(first.Width, first.Height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(frameRate, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);

            // Cached headers survive eviction of the encoder's first GOP.
            type.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, header);
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
