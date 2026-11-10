using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Finds the GPU's hardware H.264 encoders.
/// </summary>
internal static class HardwareEncoders
{
    /// <summary>
    /// Lists hardware H.264 encoders, best first.
    /// </summary>
    /// <param name="vendorId">
    /// Only list encoders from this GPU vendor, such as 0x10DE for NVIDIA, so they can read the
    /// device's textures directly.
    /// </param>
    /// <returns>Activators for the encoders. The caller disposes each one.</returns>
    /// <remarks>
    /// Matches by vendor because hardware encoders report no adapter before they are activated.
    /// Two GPUs from the same vendor would both match.
    /// </remarks>
    private static IReadOnlyList<IMFActivate> FindH264(uint vendorId)
    {
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);

        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, output, out var array, out var count);

        // An array of IMFActivate pointers, allocated with CoTaskMemAlloc.
        try
        {
            var encoders = new List<IMFActivate>();
            for (var i = 0; i < count; i++)
            {
                // Takes over the reference the array holds.
                var encoder = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                if (GetVendorId(encoder) == vendorId)
                {
                    encoders.Add(encoder);
                }
                else
                {
                    encoder.Dispose();
                }
            }

            return encoders;
        }
        finally
        {
            Marshal.FreeCoTaskMem(array);
        }
    }

    // Selection owns the spare activations so callers only manage the encoder they use.
    public static IMFActivate SelectH264(uint vendorId)
    {
        var encoders = FindH264(vendorId);
        if (encoders.Count == 0)
        {
            throw new InvalidOperationException("The GPU has no hardware H.264 encoder.");
        }

        foreach (var spare in encoders.Skip(1))
        {
            spare.Dispose();
        }

        return encoders[0];
    }

    /// <summary>
    /// The PCI vendor ID of the GPU a hardware encoder runs on, or <see langword="null"/> if it
    /// doesn't say.
    /// </summary>
    private static uint? GetVendorId(IMFActivate encoder)
    {
        string? id;
        try
        {
            // A string such as "VEN_10DE".
            id = encoder.GetAllocatedString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }

        return id is not null
            && id.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(id.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out var vendor)
                ? vendor
                : null;
    }
}
