using System.Runtime.InteropServices;

namespace Clipping.Windows.Display;

/// <summary>
/// Looks up monitor names, such as <c>DELL U2720Q</c>, through the Win32 Display
/// Configuration API.
/// </summary>
/// <remarks>
/// See https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig.
/// </remarks>
internal static class DisplayConfigNames
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;

    /// <summary>
    /// Maps each active GDI device name, such as <c>\\.\DISPLAY1</c>, to its monitor's
    /// friendly name.
    /// </summary>
    /// <remarks>
    /// Monitors that report no friendly name are left out. Returns an empty map if the
    /// query fails, so callers always have a fallback path.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> GetFriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!TryQueryActivePaths(out var paths))
        {
            return names;
        }

        foreach (var path in paths)
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = Header(GetSourceName, path.sourceInfo.adapterId, path.sourceInfo.id,
                    Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>()),
            };
            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = Header(GetTargetName, path.targetInfo.adapterId, path.targetInfo.id,
                    Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>()),
            };

            if (DisplayConfigGetDeviceInfo(ref source) == ErrorSuccess &&
                DisplayConfigGetDeviceInfo(ref target) == ErrorSuccess &&
                !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName))
            {
                // Mirrored displays share a source; keep the first monitor's name.
                names.TryAdd(source.viewGdiDeviceName, target.monitorFriendlyDeviceName);
            }
        }

        return names;
    }

    private static bool TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths)
    {
        // The configuration can change between sizing and querying; retry until it settles.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != ErrorSuccess)
            {
                break;
            }

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            var result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ErrorSuccess)
            {
                Array.Resize(ref paths, (int)pathCount);
                return true;
            }

            if (result != ErrorInsufficientBuffer)
            {
                break;
            }
        }

        paths = [];
        return false;
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, LUID adapterId, uint id, int size) =>
        new() { type = type, size = (uint)size, adapterId = adapterId, id = id };

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    // Struct layouts mirror wingdi.h. Only the fields this class reads are named meaningfully.

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Never read; QueryDisplayConfig requires the buffer. 64 bytes including the mode union.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }
}
