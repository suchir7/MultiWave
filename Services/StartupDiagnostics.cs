namespace MultiWave.Services;

/// <summary>Small local startup trace to help diagnose WinUI packaged-launch failures.</summary>
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiWave", "startup.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* Startup tracing must never prevent the app from opening. */ }
    }
}
