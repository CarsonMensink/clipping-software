using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Clipping.Windows.Graphics;

/// <summary>
/// Gets the D3D11 texture underneath a WinRT surface, such as a WGC frame's.
/// </summary>
internal static class SurfaceInterop
{
    /// <summary>
    /// Returns the texture behind <paramref name="surface"/>. Dispose it when done; that
    /// releases only this reference, not the surface.
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        try
        {
            var iid = typeof(ID3D11Texture2D).GUID;

            // The new texture object takes over the reference GetInterface returns.
            return new ID3D11Texture2D(access.GetInterface(ref iid));
        }
        finally
        {
            // Called for every captured frame; release now rather than leaving it to the finalizer.
            Marshal.ReleaseComObject(access);
        }
    }

    // A WinRT bridge that Vortice doesn't wrap. Declared in windows.graphics.directx.direct3d11.interop.h.
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }
}
