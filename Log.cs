namespace GhostFeed;

static class Log
{
    public static bool Verbose;
    static readonly object Gate = new();
    static string PathFor => System.IO.Path.Combine(Settings.Dir, "log.txt");

    public static void Info(string msg)
    {
        System.Diagnostics.Debug.WriteLine(msg);
        if (!Verbose) return;
        Write(msg);
    }

    public static void Error(string msg, Exception? ex = null) =>
        Write("ERROR " + msg + (ex is null ? "" : ": " + ex.Message));

    static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(PathFor, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break playback */ }
    }
}
