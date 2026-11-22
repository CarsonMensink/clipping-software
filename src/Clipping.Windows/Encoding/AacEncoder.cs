using System.Runtime.InteropServices;
using Clipping.Core.Audio;
using Clipping.Core.Encoding;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Encodes a clip's audio to AAC-LC with the Media Foundation AAC encoder that ships with Windows.
/// </summary>
/// <remarks>
/// The encoder is synchronous and runs on the calling thread, which is the save path. A new one
/// is made for every track, so each clip's audio is self-contained.
/// </remarks>
internal sealed class AacEncoder : IAudioEncoder
{
    /// <summary>
    /// 192 kbps, the highest rate the Windows encoder offers.
    /// </summary>
    public const int BytesPerSecond = 24_000;

    // Every AAC frame holds this many audio frames.
    private const int FramesPerPacket = 1024;

    // MF_E_TRANSFORM_NEED_MORE_INPUT.
    private static readonly Result NeedMoreInput = unchecked((int)0xC00D6D72);

    // The first bytes of MF_MT_USER_DATA for AAC are the rest of a HEAACWAVEINFO, before the
    // AudioSpecificConfig: payload type, profile, struct type, two reserved fields.
    private const int WaveInfoLength = 12;

    /// <inheritdoc/>
    public EncodedAudio Encode(AudioTrack track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        try
        {
            return EncodeTrack(track, cancellationToken);
        }
        catch (SharpGenException e)
        {
            throw new InvalidOperationException($"Couldn't encode the audio: {e.Message}", e);
        }
    }

    private static EncodedAudio EncodeTrack(AudioTrack track, CancellationToken cancellationToken)
    {
        var format = track.Format;
        using var transform = CreateTransform();

        // An encoder takes its output type first, then an input type that fits it.
        using (var output = CreateType(format, AudioFormatGuids.Aac))
        {
            output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)BytesPerSecond);
            output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
            transform.SetOutputType(0, output, 0);
        }

        using (var input = CreateType(format, AudioFormatGuids.Pcm))
        {
            input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(format.Channels * sizeof(short)));
            input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(format.SampleRate * format.Channels * sizeof(short)));
            transform.SetInputType(0, input, 0);
        }

        byte[] codecConfig;
        using (var current = transform.GetOutputCurrentType(0))
        {
            var userData = current.GetBlob(MediaTypeAttributeKeys.UserData);
            codecConfig = userData.AsSpan(Math.Min(WaveInfoLength, userData.Length)).ToArray();
        }

        var outputSize = Math.Max(transform.GetOutputStreamInfo(0).Size, 8192);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        var samples = new List<EncodedAudioSample>(track.Frames / FramesPerPacket + 4);
        for (var frame = 0; frame < track.Frames; frame += FramesPerPacket)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frames = Math.Min(FramesPerPacket, track.Frames - frame);
            using var sample = CreateInput(track.Samples, frame * format.Channels, frames * format.Channels);
            sample.SampleTime = format.DurationOf(frame).Ticks;
            sample.SampleDuration = format.DurationOf(frames).Ticks;
            transform.ProcessInput(0, sample, 0);
            ReadOutput(transform, outputSize, samples);
        }

        transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        ReadOutput(transform, outputSize, samples);

        return new EncodedAudio(format, BytesPerSecond, codecConfig, samples);
    }

    // The encoder that ships with Windows, rather than whatever else is installed.
    private static IMFTransform CreateTransform()
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, flags, input, output, out var array, out var count);

        // An array of IMFActivate pointers, allocated with CoTaskMemAlloc.
        var activates = new IMFActivate[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                activates[i] = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
            }

            if (count == 0)
            {
                throw new InvalidOperationException("Windows has no AAC audio encoder installed.");
            }

            activates[0].ActivateObject<IMFTransform>(typeof(IMFTransform).GUID, out var transform).CheckError();
            return transform!;
        }
        finally
        {
            foreach (var activate in activates)
            {
                activate?.Dispose();
            }

            Marshal.FreeCoTaskMem(array);
        }
    }

    private static IMFMediaType CreateType(AudioFormat format, Guid subtype)
    {
        var type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, subtype);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)format.SampleRate);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)format.Channels);
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    private static IMFSample CreateInput(short[] samples, int offset, int count)
    {
        var length = count * sizeof(short);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var data, out _, out _);
        try
        {
            Marshal.Copy(samples, offset, data, count);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        return sample;
    }

    // Takes every frame the encoder has ready; it holds input back until it has enough.
    private static void ReadOutput(IMFTransform transform, int outputSize, List<EncodedAudioSample> samples)
    {
        while (true)
        {
            using var sample = MediaFactory.MFCreateSample();
            using (var memory = MediaFactory.MFCreateMemoryBuffer(outputSize))
            {
                sample.AddBuffer(memory);
            }

            var buffer = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var result = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            buffer.Events?.Dispose();
            if (result == NeedMoreInput)
            {
                return;
            }

            result.CheckError();

            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var data, out _, out var length);
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                samples.Add(new EncodedAudioSample(
                    TimeSpan.FromTicks(sample.SampleTime), TimeSpan.FromTicks(sample.SampleDuration), bytes));
            }
            finally
            {
                contiguous.Unlock();
            }
        }
    }
}
