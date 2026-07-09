using System.IO;

namespace NxMassConfigurator.Wpf;

internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "nx-mass-config-wpf.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    public static void Write(Exception exception, string context)
        => Write($"{context}: {exception}");
}
