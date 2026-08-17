using System.Globalization;
using System.Text.Json;
using Mona.Core.Calendar;
using Mona.Core.Diagnostics;
using Mona.Core.Settings;

namespace Mona.Core.Weather;

/// <summary>
/// Fetches the current condition and keeps the last good answer.
///
/// Deliberately forgiving: the sticker is a decoration, so every failure path
/// ends at "keep showing what we last knew, try again later" rather than at an
/// error. A machine that has been offline since launch shows 晴, which is the
/// least alarming thing to be wrong about.
///
/// "Here" is resolved by IP rather than by asking Windows. The geolocator is
/// right there, but using it means a capability declaration, a consent prompt and
/// a dependency — all so that a decoration can choose between four icons. Picking
/// a city is the honest answer for a desktop toy, and an IP lookup is close enough
/// to decide whether to draw a cloud.
/// </summary>
public sealed class WeatherSource : IDisposable
{
    private readonly Preferences _preferences;
    private readonly HttpClient _http;
    private Timer? _timer;
    private int _inFlight;
    private (double Latitude, double Longitude)? _coordinate;

    /// <summary>
    /// Half an hour. Conditions do not change faster than the icon can show, and
    /// a desktop toy has no business polling harder than that.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    public WeatherKind Kind { get; private set; }
    public DateTime? LastUpdated { get; private set; }
    public string? PlaceName { get; private set; }

    /// <summary>Raised when the condition changes, off the UI thread.</summary>
    public event Action? Changed;

    public WeatherSource(Preferences preferences)
    {
        _preferences = preferences;
        Kind = preferences.Weather.Kind;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mona/1.0");
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, RefreshInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Forget where we thought we were and look again. Called when the city
    /// changes, since the cached fix belongs to the old choice.
    /// </summary>
    public void LocationChanged()
    {
        _coordinate = null;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        // One at a time; a second caller simply leaves.
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return;
        try
        {
            var where = await ResolveLocationAsync().ConfigureAwait(false);
            if (where is null) return;
            int? code = await FetchWeatherCodeAsync(where.Value).ConfigureAwait(false);
            if (code is null) return;

            var kind = CalendarVocabulary.FromWmo(code.Value);
            LastUpdated = DateTime.Now;
            _preferences.CalendarWeather = kind.Key();
            _preferences.Save();
            if (kind != Kind)
            {
                Kind = kind;
                Changed?.Invoke();
            }
        }
        catch (Exception error)
        {
            // A decoration that cannot reach the network is a decoration showing
            // yesterday's weather, which is fine, and not something to interrupt
            // anyone about.
            Log.Write($"weather: {error.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// The chosen city, or — only if that choice is 当前位置 — a look-up by IP.
    ///
    /// The city is checked first and returns immediately, so a default install
    /// never makes a location request at all.
    /// </summary>
    private async Task<(double Latitude, double Longitude)?> ResolveLocationAsync()
    {
        var choice = CalendarChoice.Decode(_preferences.CalendarCity);
        if (choice is CalendarChoice.Fixed fixedPlace)
        {
            PlaceName = fixedPlace.Name;
            return (fixedPlace.Latitude, fixedPlace.Longitude);
        }
        if (_coordinate is not null) return _coordinate;

        var located = await FetchIPLocationAsync().ConfigureAwait(false);
        if (located is null) return null;
        _coordinate = (located.Value.Latitude, located.Value.Longitude);
        PlaceName = located.Value.City;
        return _coordinate;
    }

    private async Task<int?> FetchWeatherCodeAsync((double Latitude, double Longitude) at)
    {
        string url = "https://api.open-meteo.com/v1/forecast"
                   + $"?latitude={at.Latitude.ToString("F4", CultureInfo.InvariantCulture)}"
                   + $"&longitude={at.Longitude.ToString("F4", CultureInfo.InvariantCulture)}"
                   + "&current=weather_code&timezone=auto";
        using var document = await GetJsonAsync(url).ConfigureAwait(false);
        if (document is null) return null;
        if (document.RootElement.TryGetProperty("current", out var current)
            && current.TryGetProperty("weather_code", out var code)
            && code.TryGetInt32(out int value))
            return value;
        return null;
    }

    private async Task<(double Latitude, double Longitude, string City)?> FetchIPLocationAsync()
    {
        using var document = await GetJsonAsync("https://ipapi.co/json/").ConfigureAwait(false);
        if (document is null) return null;
        var root = document.RootElement;
        if (!root.TryGetProperty("latitude", out var lat) || !lat.TryGetDouble(out double latitude)) return null;
        if (!root.TryGetProperty("longitude", out var lon) || !lon.TryGetDouble(out double longitude)) return null;
        string city = root.TryGetProperty("city", out var name) ? name.GetString() ?? "IP 定位" : "IP 定位";
        return (latitude, longitude, city);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url)
    {
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
