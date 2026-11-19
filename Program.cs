namespace GhostFeed;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "GhostFeed.SingleInstance", out bool created);
        if (!created) return; // already running: its global hotkeys own the keyboard

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }

    static void Fatal(Exception? ex)
    {
        Log.Error("unhandled " + ex);
        MessageBox.Show(
            $"GhostFeed hit an error and will close.\n\n{ex?.Message}\n\nDetails: {Path.Combine(Settings.Dir, "log.txt")}",
            "GhostFeed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}
