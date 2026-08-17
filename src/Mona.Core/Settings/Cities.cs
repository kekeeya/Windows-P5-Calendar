using Mona.Core.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mona.Core.Settings;

/// <summary>Somewhere to fetch the weather for.</summary>
public readonly record struct City(string Id, string Name, double Latitude, double Longitude);

/// <summary>
/// The shortlist the picker shows before anything is typed, and the one entry
/// that is not a place.
///
/// A list rather than a coordinate field. Latitude and longitude are the one
/// thing the weather needs and the one thing nobody knows off the top of their
/// head, and an accessory app that opens by asking for your location is a strange
/// thing to be prompted by.
/// </summary>
public static class Cities
{
    public const string CurrentId = "current";
    public const string DefaultId = "shanghai";

    public static readonly City Current = new(CurrentId, "当前位置", 0, 0);

    public sealed record Group(string Title, City[] Cities);

    /// <summary>
    /// Deliberately short: a shortcut for the common case, not a directory —
    /// anywhere else is a search away, out of a table of thirty-four thousand.
    /// </summary>
    public static readonly Group[] Groups =
    [
        new("中国", [
            new("beijing", "北京", 39.9042, 116.4074),
            new("shanghai", "上海", 31.2304, 121.4737),
            new("guangzhou", "广州", 23.1291, 113.2644),
            new("shenzhen", "深圳", 22.5431, 114.0579),
            new("hangzhou", "杭州", 30.2741, 120.1551),
            new("chengdu", "成都", 30.5728, 104.0668),
            new("chongqing", "重庆", 29.5630, 106.5516),
            new("wuhan", "武汉", 30.5928, 114.3055),
            new("nanjing", "南京", 32.0603, 118.7969),
            new("xian", "西安", 34.3416, 108.9398)
        ]),
        new("美国", [
            new("newyork", "纽约", 40.7128, -74.0060),
            new("losangeles", "洛杉矶", 34.0522, -118.2437),
            new("sanfrancisco", "旧金山", 37.7749, -122.4194),
            new("seattle", "西雅图", 47.6062, -122.3321),
            new("chicago", "芝加哥", 41.8781, -87.6298)
        ]),
        new("其他", [
            new("tokyo", "东京", 35.6762, 139.6503),
            new("seoul", "首尔", 37.5665, 126.9780),
            new("london", "伦敦", 51.5074, -0.1278),
            new("paris", "巴黎", 48.8566, 2.3522),
            new("berlin", "柏林", 52.5200, 13.4050),
            new("rome", "罗马", 41.9028, 12.4964),
            new("canberra", "堪培拉", -35.2809, 149.1300),
            new("moscow", "莫斯科", 55.7558, 37.6173)
        ])
    ];

    public static readonly City[] All = Groups.SelectMany(group => group.Cities).ToArray();

    /// <summary>
    /// Falls back to the default rather than to nowhere: a key left over from an
    /// older build should show Shanghai's weather, not none at all.
    /// </summary>
    public static City? Named(string id)
    {
        if (id == CurrentId) return Current;
        foreach (var city in All) if (city.Id == id) return city;
        foreach (var city in All) if (city.Id == DefaultId) return city;
        return null;
    }
}

/// <summary>What the preference actually holds, and how it is written down.</summary>
public abstract record CalendarChoice
{
    /// <summary>Follow the machine — the only choice that goes looking.</summary>
    public sealed record Here : CalendarChoice;
    public sealed record Fixed(string Name, double Latitude, double Longitude) : CalendarChoice;

    /// <summary>
    /// What to call the choice in the interface.
    ///
    /// Named apart from <c>Fixed.Name</c> on purpose: a property called <c>Name</c>
    /// here would shadow the positional one on the subtype, and the switch below
    /// would then call itself for ever rather than read the place's own name.
    /// </summary>
    public string DisplayName => this switch
    {
        Fixed place => place.Name,
        _ => Cities.Current.Name
    };

    /// <summary>
    /// Name last, and split with a limit, so a name containing the separator
    /// survives the round trip.
    /// </summary>
    public static string Encode(string name, double latitude, double longitude)
        => string.Create(CultureInfo.InvariantCulture, $"{latitude}|{longitude}|{name}");

    public static CalendarChoice Decode(string raw)
    {
        if (raw == Cities.CurrentId) return new Here();
        var parts = raw.Split('|', 3);
        if (parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return new Fixed(parts[2], lat, lon);

        // An id from an older build, or nonsense. Either way the shortlist knows
        // what to do — and its fallback is Shanghai, not nothing.
        var city = Cities.Named(raw) ?? Cities.Named(Cities.DefaultId)!.Value;
        return new Fixed(city.Name, city.Latitude, city.Longitude);
    }
}

/// <summary>One result out of the searchable table.</summary>
public readonly record struct Place(string Name, string Ascii, string State, string Country,
                                    double Latitude, double Longitude)
{
    /// <summary>"省州 · 国家", there only to tell the two Portlands apart.</summary>
    public string Detail
    {
        get
        {
            string region = Country;
            try
            {
                if (Country.Length == 2)
                    region = new RegionInfo(Country).DisplayName;
            }
            catch (ArgumentException) { /* not a region code the system knows */ }
            return string.IsNullOrEmpty(State) ? region : $"{State} · {region}";
        }
    }
}

/// <summary>
/// The searchable table: every city over fifteen thousand people, about
/// thirty-four thousand of them, shipped beside the app.
///
/// Local rather than a geocoding request per keystroke. The data is the same —
/// Open-Meteo's geocoding is GeoNames underneath — but this way a result appears
/// on the keystroke rather than after a round trip, it works with no network,
/// there is no rate limit to back off from, and what you type stays here.
///
/// The file is pre-sorted by population, which is the whole ranking scheme: a scan
/// that stops at twenty hits returns the twenty biggest matches without sorting
/// anything.
/// </summary>
public sealed class PlaceTable
{
    private readonly string _path;
    private List<Place>? _rows;
    private readonly object _lock = new();

    public PlaceTable(string artDirectory) => _path = Path.Combine(artDirectory, "cal-cities.tsv");

    /// <summary>Parsed once, lazily — nobody pays for it unless they open the picker.</summary>
    private List<Place> Rows()
    {
        lock (_lock)
        {
            if (_rows is not null) return _rows;
            _rows = new List<Place>(35_000);
            if (!File.Exists(_path))
            {
                Log.Write("cal-cities.tsv missing; only the shortlist is searchable");
                return _rows;
            }
            foreach (string line in File.ReadLines(_path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                var f = line.Split('\t');
                if (f.Length < 6) continue;
                if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                if (!double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                _rows.Add(new Place(f[0], f[1], f[2], f[3], lat, lon));
            }
            return _rows;
        }
    }

    /// <summary>
    /// Substring, case- and accent-insensitive, over both the local name and the
    /// latin one. The latin column is why "zhuhai" finds 珠海 without anyone
    /// having to build a pinyin index — for Chinese cities GeoNames' ascii name
    /// <em>is</em> the pinyin.
    ///
    /// Ranked exact, then prefix, then substring; within each, by population,
    /// which the file order already gives.
    /// </summary>
    public List<Place> Search(string raw, int limit = 20)
    {
        string query = Fold(raw).Trim();
        var results = new List<Place>();
        if (query.Length == 0) return results;

        var exact = new List<Place>();
        var prefix = new List<Place>();
        var loose = new List<Place>();

        foreach (var row in Rows())
        {
            string a = Fold(row.Name), b = Fold(row.Ascii);
            if (a == query || b == query) exact.Add(row);
            else if (a.StartsWith(query, StringComparison.Ordinal)
                  || b.StartsWith(query, StringComparison.Ordinal)) prefix.Add(row);
            else if (a.Contains(query, StringComparison.Ordinal)
                  || b.Contains(query, StringComparison.Ordinal)) loose.Add(row);
            else continue;

            // Enough of every rank to fill the list even if one of them is empty.
            if (exact.Count >= limit) break;
        }

        results.AddRange(exact);
        results.AddRange(prefix);
        results.AddRange(loose);
        return results.Count > limit ? results.GetRange(0, limit) : results;
    }

    /// <summary>Case and accents removed, so "Malmo" finds "Malmö".</summary>
    private static string Fold(string value)
    {
        string normalised = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        foreach (char c in normalised)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
