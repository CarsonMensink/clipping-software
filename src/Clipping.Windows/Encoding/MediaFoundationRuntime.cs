using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Keeps Media Foundation started for as long as this object lives.
/// </summary>
/// <remarks>
/// Create one before any other Media Foundation call and dispose it after the last.
/// </remarks>
internal sealed class MediaFoundationRuntime : IDisposable
{
    /// <exception cref="SharpGen.Runtime.SharpGenException">Media Foundation is not installed, such as on Windows N editions.</exception>
    public MediaFoundationRuntime()
    {
        // The full mode; the light one leaves out the encoders.
        MediaFactory.MFStartup(useLightVersion: false).CheckError();
    }

    public void Dispose()
    {
        MediaFactory.MFShutdown();
    }
}
