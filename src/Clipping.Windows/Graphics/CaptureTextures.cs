using Clipping.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Clipping.Windows.Graphics;

/// <summary>
/// Creates the textures that captured frames are copied into.
/// </summary>
internal static class CaptureTextures
{
    /// <summary>
    /// Creates <paramref name="count"/> BGRA8 textures of one size on <paramref name="device"/>.
    /// </summary>
    /// <remarks>
    /// Bound as shader resources and render targets so the encoder can read them directly
    /// or convert them to its own format on the GPU.
    /// </remarks>
    public static TextureSet<ID3D11Texture2D> Create(ID3D11Device device, int width, int height, int count)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };

        var textures = new List<ID3D11Texture2D>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                textures.Add(device.CreateTexture2D(description));
            }
        }
        catch
        {
            // Out of video memory partway through; release the ones already made.
            foreach (var texture in textures)
            {
                texture.Dispose();
            }

            throw;
        }

        return new TextureSet<ID3D11Texture2D>(textures);
    }
}
