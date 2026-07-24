namespace BobrCam;

public static class FrameBridge
{
#if WINDOWS
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BobrCam", "Frames");
    private static readonly string FramePath = Path.Combine(DirectoryPath, "latest.jpg");
    private static readonly object Sync = new();

    public static void Publish(byte[] jpeg)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var temporaryPath = FramePath + ".tmp";
                File.WriteAllBytes(temporaryPath, jpeg);
                File.Move(temporaryPath, FramePath, true);
            }
        }
        catch
        {
            // Preview must continue even if the optional virtual-camera bridge is unavailable.
        }
    }
#else
    public static void Publish(byte[] jpeg) { }
#endif
}
