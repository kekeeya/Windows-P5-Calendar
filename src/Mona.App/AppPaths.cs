namespace Mona.App;

/// <summary>
/// Where the art pack is.
///
/// Beside the executable in an installed copy, and up the tree from
/// <c>bin/Release/…</c> when it is being run out of the build directory. The
/// second case is not developer convenience for its own sake: the parity checker
/// and the app have to be looking at the same pack, or a difference between them
/// means nothing.
/// </summary>
internal static class AppPaths
{
    public static string Assets { get; } = Locate();

    public static string CalendarArt => Path.Combine(Assets, "CalendarArt");
    /// <summary>Mona's head — the tray icon.</summary>
    public static string StillIcon => Path.Combine(Assets, "Tray", "MonaHead.png");

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "assets");
            if (Directory.Exists(Path.Combine(candidate, "CalendarArt"))) return candidate;
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "assets");
    }
}
