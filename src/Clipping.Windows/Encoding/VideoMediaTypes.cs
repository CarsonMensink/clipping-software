using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

internal static class VideoMediaTypes
{
    // Encoding and MP4 playback must agree on geometry, timing and square progressive pixels.
    public static IMFMediaType Create(Guid subtype, int width, int height, int frameRate)
    {
        var type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, subtype);
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(frameRate, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    // Media Foundation stores paired 32-bit values in one 64-bit attribute.
    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
