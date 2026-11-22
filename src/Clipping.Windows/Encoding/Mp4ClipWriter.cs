using System.Runtime.InteropServices;
using Clipping.Core.Clips;
using Clipping.Core.Encoding;
using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Writes buffered H.264 video, and AAC audio when the clip has some, to an MP4 file with the
/// Media Foundation Sink Writer.
/// </summary>
/// <remarks>
/// The video is already encoded, so it passes straight into the file without being encoded
/// again and loses no quality. Audio is buffered uncompressed and encoded here, before the file
/// is opened, so a failure there still leaves a video-only clip rather than none. Runs on a
/// thread-pool thread, never the capture or encoding threads.
/// <para>
/// Both streams are timed from the clip's first video frame, which is also where its audio
/// track starts, so they stay in step in the file.
/// </para>
/// </remarks>
internal sealed class Mp4ClipWriter : IClipWriter
{
    private readonly IAudioEncoder? _audioEncoder;

    /// <param name="audioEncoder">Encodes clips' audio; <see langword="null"/> to always write video only.</param>
    public Mp4ClipWriter(IAudioEncoder? audioEncoder = null)
    {
        _audioEncoder = audioEncoder;
    }

    /// <inheritdoc/>
    public string Extension => ".mp4";

    /// <inheritdoc/>
    public Task<ClipWriteResult> WriteAsync(ClipSnapshot clip, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (clip.Samples.Count == 0 || !clip.Samples[0].IsKeyframe)
        {
            throw new ArgumentException("A clip must start on a keyframe.", nameof(clip));
        }

        return Task.Run(() =>
        {
            var (audio, result) = EncodeAudio(clip, cancellationToken);
            try
            {
                ClipFileWrite.WriteOrDelete(path, () => Write(clip, audio, path, cancellationToken));
                return result;
            }
            catch (IOException) when (audio is not null)
            {
                // The partial file is gone; if the audio stream was the problem, video alone may still fit.
                ClipFileWrite.WriteOrDelete(path, () => Write(clip, null, path, cancellationToken));
                return new ClipWriteResult("the audio couldn't be added to the file.");
            }
        }, cancellationToken);
    }

    // A clip whose audio can't be encoded is still worth keeping for its video.
    private (EncodedAudio? Audio, ClipWriteResult Result) EncodeAudio(ClipSnapshot clip, CancellationToken cancellationToken)
    {
        if (clip.Audio is not { Frames: > 0 } track || _audioEncoder is null)
        {
            return (null, ClipWriteResult.Complete);
        }

        try
        {
            var audio = _audioEncoder.Encode(track, cancellationToken);
            return audio.Samples.Count == 0
                ? (null, new ClipWriteResult("the audio encoder produced nothing."))
                : (audio, ClipWriteResult.Complete);
        }
        catch (InvalidOperationException e)
        {
            return (null, new ClipWriteResult(e.Message));
        }
    }

    // Compressed samples pass through unchanged, with timestamps rebased for standalone playback.
    private static void Write(ClipSnapshot clip, EncodedAudio? audio, string path, CancellationToken cancellationToken)
    {
        var first = clip.Samples[0];

        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        // Validate codec headers before opening a file that would need cleanup on failure.
        using var videoType = CreateVideoType(clip);
        using var audioType = audio is null ? null : CreateAudioType(audio);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

        // The same type in and out tells the sink writer to pass samples through untouched.
        var videoStream = writer.AddStream(videoType);
        writer.SetInputMediaType(videoStream, videoType, null!);
        var audioStream = -1;
        if (audioType is not null)
        {
            audioStream = writer.AddStream(audioType);
            writer.SetInputMediaType(audioStream, audioType, null!);
        }

        writer.BeginWriting();

        // Interleaved by time, so the file needn't buffer one stream while the other catches up.
        var audioSamples = audio?.Samples ?? [];
        var nextAudio = 0;
        foreach (var encoded in clip.Samples)
        {
            var time = encoded.Timestamp - first.Timestamp;
            for (; nextAudio < audioSamples.Count && audioSamples[nextAudio].Timestamp <= time; nextAudio++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packet = audioSamples[nextAudio];
                WriteSample(writer, audioStream, packet.Data, packet.Timestamp, packet.Duration, keyframe: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteSample(writer, videoStream, encoded.Data, time, encoded.Duration, encoded.IsKeyframe);
        }

        for (; nextAudio < audioSamples.Count; nextAudio++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packet = audioSamples[nextAudio];
            WriteSample(writer, audioStream, packet.Data, packet.Timestamp, packet.Duration, keyframe: true);
        }

        writer.Finalize();
    }

    private static void WriteSample(IMFSinkWriter writer, int stream, byte[] data, TimeSpan time, TimeSpan duration, bool keyframe)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = data.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time.Ticks;
        sample.SampleDuration = duration.Ticks;
        sample.Set(SampleAttributeKeys.CleanPoint, keyframe ? 1u : 0u);
        writer.WriteSample(stream, sample);
    }

    // The MP4 header carries retained codec parameters even when the first saved GOP omits them.
    private static IMFMediaType CreateVideoType(ClipSnapshot clip)
    {
        var first = clip.Samples[0];
        var frameRate = first.Duration > TimeSpan.Zero
            ? (int)Math.Round((double)TimeSpan.TicksPerSecond / first.Duration.Ticks)
            : 60;
        var seconds = Math.Max(clip.Duration.TotalSeconds, 0.001);
        var bitrate = (uint)Math.Min(clip.Samples.Sum(s => (long)s.Data.Length) * 8 / seconds, uint.MaxValue);

        var header = first.SequenceHeader ?? new H264ParameterSets().Update(first.Data)
            ?? throw new IOException("The clip has no complete SPS/PPS sequence header.");
        var type = VideoMediaTypes.Create(VideoFormatGuids.H264, first.Width, first.Height, frameRate);
        try
        {
            type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);

            type.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, header);
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    // Raw AAC frames, described by the AudioSpecificConfig after a HEAACWAVEINFO header.
    private static IMFMediaType CreateAudioType(EncodedAudio audio)
    {
        var type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.Format.SampleRate);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Format.Channels);
            type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)audio.BytesPerSecond);
            type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u);
            type.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
            type.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, AacProfileLevel);

            // wPayloadType 0 (raw), wAudioProfileLevelIndication, wStructType 0, then reserved.
            var userData = new byte[12 + audio.CodecConfig.Length];
            BitConverter.TryWriteBytes(userData.AsSpan(2), (ushort)AacProfileLevel);
            audio.CodecConfig.CopyTo(userData, 12);
            type.SetBlob(MediaTypeAttributeKeys.UserData, userData);
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    // AAC Profile, level 2: what the Windows encoder produces for stereo up to 48 kHz.
    private const uint AacProfileLevel = 0x29;
}
