namespace Clipping.Windows.Encoding;

internal static class ClipFileWrite
{
    // Every failed write removes its partial file, including managed IO failures after sink creation.
    public static void WriteOrDelete(string path, Action write)
    {
        try
        {
            write();
        }
        catch (Exception failure)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception cleanupFailure)
            {
                // Preserve both causes when the filesystem also refuses cleanup.
                throw new IOException("Couldn't write or remove the partial clip.",
                    new AggregateException(failure, cleanupFailure));
            }

            if (failure is OperationCanceledException or IOException)
            {
                throw;
            }

            throw new IOException($"Couldn't write the clip: {failure.Message}", failure);
        }
    }
}
