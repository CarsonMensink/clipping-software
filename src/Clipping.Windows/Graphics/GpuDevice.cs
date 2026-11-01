using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
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
    /// <exception cref="SharpGen.Runtime.SharpGenException">No hardware D3D11 device could be created.</exception>
    public GpuDevice()
    {
        D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device device,
            out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;

        using (var multithread = Context.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        Direct3DDevice = CreateWinRTDevice(Device);
    }

    /// <summary>
    /// The device, for code that calls D3D11 directly.
    /// </summary>
    /// <remarks>
    /// Owned by this object; valid until it is disposed. Do not dispose it.
    /// </remarks>
    public ID3D11Device Device { get; }

    /// <summary>
    /// The immediate context. Safe to use from any thread, since multithread protection is on.
    /// </summary>
    /// <remarks>
    /// Owned by this object; valid until it is disposed. Do not dispose it.
    /// </remarks>
    public ID3D11DeviceContext Context { get; }

    /// <summary>
    /// The device as WinRT sees it, for Windows.Graphics.Capture.
    /// </summary>
    public IDirect3DDevice Direct3DDevice { get; }

    public void Dispose()
    {
        Direct3DDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    /// <summary>
    /// Wraps a D3D11 device as the WinRT <see cref="IDirect3DDevice"/> that WGC requires.
    /// </summary>
    private static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
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

    // A WinRT bridge that Vortice doesn't wrap.
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
