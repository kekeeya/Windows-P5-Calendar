using System.Windows.Forms;
using Mona.Core.Diagnostics;
using Mona.Core.Settings;

namespace Mona.App;

internal static class Program
{
    /// <summary>
    /// Named per user rather than globally, so two people signed into the same
    /// machine each get their own Mona.
    /// </summary>
    private const string InstanceName = @"Local\MonaWindowsSingleInstance";

    [STAThread]
    private static void Main()
    {
        // A second copy would put a second cat in the tray and fight the first one
        // over the settings file.
        using var single = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first) return;

        // A GUI process has no console, so this file is the only way anything it
        // has to say reaches the person running it.
        Log.Start(Path.Combine(Preferences.Directory, "log.txt"),
                  $"Mona {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion.VersionString}"
                  + $", {Environment.ProcessPath}");

        // Both hooks: the first catches what happens on the UI thread, the second
        // everything else. Without them a crash in a tray app is an icon that
        // silently disappears.
        Application.ThreadException += (_, e) => Log.Failure("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Log.Failure("unhandled", error);
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Application.Run(new MonaContext());
        }
        catch (Exception error)
        {
            Log.Failure("startup", error);
            throw;
        }
        Log.Write("quit");
    }
}
