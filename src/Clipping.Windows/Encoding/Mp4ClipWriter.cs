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

    private static void WriteOrDelete(ClipSnapshot clip, string path, CancellationToken cancellationToken)
    {
        try
        {
            Write(clip, path, cancellationToken);
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or COMException or OperationCanceledException)
        {
            // The sink writer is released by now, so the partial file can go.
            File.Delete(path);
            if (e is OperationCanceledException)
            {
                throw;
            }

            throw new IOException($"Couldn't write the clip: {e.Message}", e);
        }
    }

    private static void Write(ClipSnapshot clip, string path, CancellationToken cancellationToken)
    {
        var first = clip.Samples[0];

        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

        // The same type in and out tells the sink writer to pass samples through untouched.
        using var type = CreateMediaType(clip);
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

    private static IMFMediaType CreateMediaType(ClipSnapshot clip)
    {
        var first = clip.Samples[0];
        var frameRate = first.Duration > TimeSpan.Zero
            ? (int)Math.Round((double)TimeSpan.TicksPerSecond / first.Duration.Ticks)
            : 60;
        var seconds = Math.Max(clip.Duration.TotalSeconds, 0.001);
        var bitrate = (uint)Math.Min(clip.Samples.Sum(s => (long)s.Data.Length) * 8 / seconds, uint.MaxValue);

        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack(first.Width, first.Height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack(frameRate, 1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);

        // The MP4 header needs the stream's SPS and PPS, which the encoder repeats on every keyframe.
        type.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, SequenceHeader(first.Data));
        return type;
    }

    /// <summary>
    /// The SPS and PPS units from an Annex B keyframe, each with its start code.
    /// </summary>
    private static byte[] SequenceHeader(byte[] keyframe)
    {
        var header = new List<byte>();
        foreach (var (start, length) in NalUnits(keyframe))
        {
            // Type 7 is the SPS and 8 the PPS.
            var type = keyframe[start] & 0x1F;
            if (type is 7 or 8)
            {
                header.AddRange([0, 0, 0, 1]);
                header.AddRange(keyframe.AsSpan(start, length).ToArray());
            }
        }

        if (header.Count == 0)
        {
            throw new IOException("The first keyframe carries no SPS or PPS.");
        }

        return header.ToArray();
    }

    /// <summary>
    /// The start and length of each NAL unit in an Annex B stream, after its start code.
    /// </summary>
    private static IEnumerable<(int Start, int Length)> NalUnits(byte[] data)
    {
        var start = -1;
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
            {
                continue;
            }

            if (start >= 0)
            {
                // A four-byte start code puts one more zero before the 00 00 01.
                var end = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                yield return (start, end - start);
            }

            start = i + 3;
            i += 2;
        }

        if (start >= 0 && start < data.Length)
        {
            yield return (start, data.Length - start);
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
