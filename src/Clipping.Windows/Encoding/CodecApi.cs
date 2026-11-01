using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Sets encoder options through <c>ICodecAPI</c>, which Vortice doesn't wrap.
/// </summary>
internal static class CodecApi
{
    // GUIDs from codecapi.h.
    public static readonly Guid RateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    public static readonly Guid MeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    public static readonly Guid GopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    public static readonly Guid LowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const uint RateControlCbr = 0;

    /// <summary>
    /// Sets each option on <paramref name="encoder"/>.
    /// </summary>
    /// <param name="values">
    /// Option GUIDs and their values: a <see cref="uint"/> for numbers, a <see cref="bool"/> for
    /// switches, which marshal to the VT_UI4 and VT_BOOL variants the encoder expects.
    /// </param>
    public static void Set(ComObject encoder, params (Guid Api, object Value)[] values)
    {
        var codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(encoder.NativePointer);
        try
        {
            foreach (var (api, value) in values)
            {
                var key = api;
                var variant = value;
                var hr = codecApi.SetValue(ref key, ref variant);
                if (hr < 0)
                {
                    throw new InvalidOperationException(
                        $"The encoder rejected {NameOf(api)} = {value}.", Marshal.GetExceptionForHR(hr));
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(codecApi);
        }
    }

    private static string NameOf(Guid api) =>
        api == RateControlMode ? nameof(RateControlMode)
        : api == MeanBitRate ? nameof(MeanBitRate)
        : api == GopSize ? nameof(GopSize)
        : api == LowLatencyMode ? nameof(LowLatencyMode)
        : api.ToString();

    // Only SetValue is called; the methods before it hold its place in the vtable.
    [ComImport]
    [Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig]
        int IsSupported(IntPtr api);

        [PreserveSig]
        int IsModifiable(IntPtr api);

        [PreserveSig]
        int GetParameterRange(IntPtr api, IntPtr minimum, IntPtr maximum, IntPtr step);

        [PreserveSig]
        int GetParameterValues(IntPtr api, IntPtr values, IntPtr count);

        [PreserveSig]
        int GetDefaultValue(IntPtr api, IntPtr value);

        [PreserveSig]
        int GetValue(IntPtr api, IntPtr value);

        [PreserveSig]
        int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
