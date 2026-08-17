using Mona.Core.Diagnostics;
using Mona.Core.Imaging;

namespace Mona.Core.Calendar;

/// <summary>
/// Loads and caches the PNGs and the layout from the art folder.
///
/// Everything is looked up by a flat, unique name (<c>cal-day-7-plate</c>)
/// rather than by folder, so that no two groups can collide: <c>Month/4.png</c>
/// and <c>Day/4.png</c> would be the same file to a lookup that only knows "4".
/// </summary>
public sealed class CalendarArt
{
    private readonly string _directory;
    private readonly Dictionary<string, Bitmap32?> _images = new();
    private readonly object _lock = new();

    /// <summary>
    /// Null when the art pack is missing, which is the one failure the sticker
    /// cannot paint through — the caller shows nothing rather than a half-drawn
    /// design.
    /// </summary>
    public CalendarLayout? Layout { get; }

    public string Directory => _directory;

    public CalendarArt(string directory)
    {
        _directory = directory;
        string layoutPath = Path.Combine(directory, "cal-layout.json");
        try
        {
            Layout = CalendarLayout.Load(layoutPath);
            if (Layout is null)
                Log($"cal-layout.json missing from {directory}");
        }
        catch (Exception error)
        {
            // Said out loud rather than swallowed. A field added to the model but
            // not to the art pack makes decoding throw, and a null layout draws
            // nothing at all — which looks like the window is broken rather than
            // like the data is.
            Log($"cal-layout.json does not match the renderer: {error.Message}");
            Layout = null;
        }
    }

    /// <summary>Where diagnostics go.</summary>
    public static Action<string> Log { get; set; } = message => Diagnostics.Log.Write($"calendar: {message}");

    /// <summary>
    /// Null for a name the pack does not carry. Not every group has every layer —
    /// the month digits have no <c>-text</c>, the weather icons are a single
    /// shape — so a miss is ordinary and is remembered rather than retried.
    /// </summary>
    public Bitmap32? Image(string name)
    {
        lock (_lock)
        {
            if (_images.TryGetValue(name, out var hit)) return hit;
            Bitmap32? image = null;
            string path = Path.Combine(_directory, name + ".png");
            if (File.Exists(path))
            {
                try { image = Png.Decode(path); }
                catch (Exception error) { Log($"{name}.png unreadable: {error.Message}"); }
            }
            _images[name] = image;
            return image;
        }
    }

    /// <summary>Sunday is 1, the numbering the date maths uses on both platforms.</summary>
    public static string WeekKey(int weekday)
    {
        string[] keys = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
        return keys[Math.Clamp(weekday - 1, 0, 6)];
    }
}
