using System.Text;

namespace Mona.Core.Diagnostics;

/// <summary>
/// A line-per-event log beside the settings file.
///
/// A tray app is a GUI subsystem process: it has no console, so anything written
/// to standard error goes nowhere at all. That is fine until someone else runs it
/// and it does not work, at which point the only thing to go on is "the icon
/// didn't appear" — no path it looked in, no exception, nothing. So the same
/// lines go to a file the person testing can send back.
///
/// Deliberately dull: no levels, no rotation policy beyond a size cap, no
/// dependency. It exists to answer "what did it do before it stopped".
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _path;
    /// <summary>Half a megabyte, after which the file starts again. It is a log for one session.</summary>
    private const long MaxBytes = 512 * 1024;

    /// <summary>
    /// Points the log at a file and writes the header. Called once at startup;
    /// until it is, lines are dropped rather than buffered — nothing before this
    /// is worth keeping.
    /// </summary>
    public static void Start(string path, string banner)
    {
        lock (Lock)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);
            }
            catch (Exception)
            {
                // A log that cannot be written is not a reason to fail to start.
                _path = null;
                return;
            }
        }
        Write(new string('—', 60));
        Write(banner);
    }

    public static string? Path_ => _path;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (Lock)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Losing a log line is not worth an exception on top of whatever
                // was being logged.
            }
        }
    }

    /// <summary>An exception with everything that identifies it, on one entry.</summary>
    public static void Failure(string what, Exception error)
        => Write($"{what}: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
}
