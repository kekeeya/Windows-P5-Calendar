using Mona.Core.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mona.Core.Settings;

/// <summary>
/// What the app remembers between launches.
///
/// A JSON file under <c>%APPDATA%\Mona</c> rather than the registry: it is a
/// handful of values, it is worth being able to read and delete by hand, and the
/// registry has nothing to offer here beyond being where Windows apps
/// traditionally put things. Written whole and atomically, so a crash mid-save
/// leaves the previous settings rather than half of the new ones.
///
/// Two of the defaults look timid and are meant to. The sticker starts hidden
/// because it is a window on someone's desktop, and a thing that reappears every
/// launch after you closed it is a thing you have to close every launch; the city
/// starts as a city rather than "here" because "here" is the only value that
/// goes looking for you.
/// </summary>
public sealed class Preferences
{
    public bool CalendarVisible { get; set; }
    public double CalendarWidth { get; set; } = 320;
    public bool CalendarAlwaysOnTop { get; set; } = true;
    /// <summary>Where the sticker was left, in virtual-screen pixels; null until it is moved.</summary>
    public int? CalendarX { get; set; }
    public int? CalendarY { get; set; }
    /// <summary>
    /// Which city's weather to show. Either <c>"current"</c> or
    /// <c>"lat|lon|name"</c> — one string rather than three fields so that
    /// reading it is atomic; a half-updated pair of numbers would be a real place
    /// nobody picked.
    /// </summary>
    public string CalendarCity { get; set; } = Cities.DefaultId;
    /// <summary>
    /// Last known condition, so a launch with no network still draws something
    /// truer than a hardcoded default.
    /// </summary>
    public string CalendarWeather { get; set; } = "sunny";
    public bool LaunchAtLogin { get; set; }

    [JsonIgnore]
    public WeatherKindMemory Weather => new(this);

    /// <summary>Reads the stored condition without the caller minding the spelling.</summary>
    public readonly struct WeatherKindMemory(Preferences owner)
    {
        public Calendar.WeatherKind Kind
            => Calendar.CalendarVocabulary.ParseWeather(owner.CalendarWeather) ?? Calendar.WeatherKind.Sunny;
    }

    // MARK: - storage

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mona");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>Anything unreadable falls back to the defaults rather than refusing to start.</summary>
    public static Preferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), Options)
                       ?? new Preferences();
        }
        catch (Exception error)
        {
            Log.Write($"settings unreadable, starting fresh: {error.Message}");
        }
        return new Preferences();
    }

    private readonly object _lock = new();

    /// <summary>
    /// Whole file, written beside the real one and moved into place. A desktop
    /// toy has no business losing someone's settings because it was quit while
    /// saving.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception error)
            {
                Log.Write($"could not save settings: {error.Message}");
            }
        }
    }
}
