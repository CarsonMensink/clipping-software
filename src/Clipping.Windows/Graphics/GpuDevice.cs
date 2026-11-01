using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Clipping.Windows.Graphics;

/// <summary>
/// The one D3D11 device shared by capture and encoding.
/// </summary>
/// <remarks>
/// A texture can only be used directly on the device that created it, so every component
/// that touches captured textures must use this device. Multithread protection is on,
/// because WGC's FrameArrived and the encoder use the immediate context from different
/// threads.
/// </remarks>
internal sealed class GpuDevice : IDisposable
{
    private readonly IntPtr _context;

    /// <exception cref="COMException">No hardware D3D11 device could be created.</exception>
    public GpuDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero,
            0,
            D3D11_SDK_VERSION,
            out var device,
            out _,
            out _context);
        Marshal.ThrowExceptionForHR(hr);
        NativeDevice = device;

        var multithread = (ID3D10Multithread)Marshal.GetObjectForIUnknown(_context);
        multithread.SetMultithreadProtected(true);
        Marshal.FinalReleaseComObject(multithread);

        Direct3DDevice = CreateWinRTDevice(device);
    }

    /// <summary>
    /// The device as WinRT sees it, for Windows.Graphics.Capture.
    /// </summary>
    public IDirect3DDevice Direct3DDevice { get; }

    /// <summary>
    /// The <c>ID3D11Device</c> pointer, for code that calls D3D11 directly.
    /// </summary>
    /// <remarks>
    /// Owned by this object; valid until it is disposed. Do not release it.
    /// </remarks>
    public IntPtr NativeDevice { get; }

    public void Dispose()
    {
        Direct3DDevice.Dispose();
        Marshal.Release(_context);
        Marshal.Release(NativeDevice);
    }

    /// <summary>
    /// Wraps a D3D11 device as the WinRT <see cref="IDirect3DDevice"/> that WGC requires.
    /// </summary>
    private static IDirect3DDevice CreateWinRTDevice(IntPtr device)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IDXGIDeviceIid, out var dxgiDevice));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                // FromAbi takes its own reference.
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

    private static readonly Guid IDXGIDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint numFeatureLevels,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // Declared in d3d10misc.h. Every method returns a value directly rather than an HRESULT.
    [ComImport]
    [Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D10Multithread
    {
        [PreserveSig]
        void Enter();

        [PreserveSig]
        void Leave();

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.Bool)]
        bool SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool protect);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetMultithreadProtected();
    }
}
