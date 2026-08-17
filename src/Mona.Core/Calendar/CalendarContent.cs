namespace Mona.Core.Calendar;

/// <summary>The four conditions the artwork can draw.</summary>
public enum WeatherKind { Sunny, Cloudy, Rain, Snow }

/// <summary>The five times of day the artwork has a card for.</summary>
public enum DaySlot { Dawn, Morning, Noon, Afternoon, Night }

public static class CalendarVocabulary
{
    /// <summary>The names the layout table is keyed by — lower case, unchanged.</summary>
    public static string Key(this WeatherKind kind) => kind switch
    {
        WeatherKind.Sunny => "sunny",
        WeatherKind.Cloudy => "cloudy",
        WeatherKind.Rain => "rain",
        WeatherKind.Snow => "snow",
        _ => "sunny"
    };

    public static string Label(this WeatherKind kind) => kind switch
    {
        WeatherKind.Sunny => "晴",
        WeatherKind.Cloudy => "多云",
        WeatherKind.Rain => "雨",
        WeatherKind.Snow => "雪",
        _ => "晴"
    };

    public static string Key(this DaySlot slot) => slot switch
    {
        DaySlot.Dawn => "dawn",
        DaySlot.Morning => "morning",
        DaySlot.Noon => "noon",
        DaySlot.Afternoon => "afternoon",
        DaySlot.Night => "night",
        _ => "night"
    };

    public static WeatherKind? ParseWeather(string raw) => raw.ToLowerInvariant() switch
    {
        "sunny" => WeatherKind.Sunny,
        "cloudy" => WeatherKind.Cloudy,
        "rain" => WeatherKind.Rain,
        "snow" => WeatherKind.Snow,
        _ => null
    };

    public static DaySlot? ParseSlot(string raw) => raw.ToLowerInvariant() switch
    {
        "dawn" => DaySlot.Dawn,
        "morning" => DaySlot.Morning,
        "noon" => DaySlot.Noon,
        "afternoon" => DaySlot.Afternoon,
        "night" => DaySlot.Night,
        _ => null
    };

    /// <summary>
    /// WMO codes, which is what Open-Meteo reports.
    ///
    /// Collapsed to four buckets because there are only four icons: drizzle,
    /// freezing rain and showers are all 雨 as far as the artwork is concerned.
    /// </summary>
    public static WeatherKind FromWmo(int code) => code switch
    {
        0 or 1 => WeatherKind.Sunny,
        2 or 3 or 45 or 48 => WeatherKind.Cloudy,
        >= 71 and <= 77 or 85 or 86 => WeatherKind.Snow,
        >= 51 and <= 67 or >= 80 and <= 82 or >= 95 and <= 99 => WeatherKind.Rain,
        _ => WeatherKind.Cloudy
    };

    /// <summary>
    /// Boundaries picked to match what the cards say: 早晨 before the working day,
    /// 上午 through the morning, 中午 over lunch, 下午 through the afternoon, 夜晚
    /// once it is dark.
    /// </summary>
    public static DaySlot SlotForHour(int hour) => hour switch
    {
        >= 5 and < 9 => DaySlot.Dawn,
        >= 9 and < 11 => DaySlot.Morning,
        >= 11 and < 14 => DaySlot.Noon,
        >= 14 and < 18 => DaySlot.Afternoon,
        _ => DaySlot.Night
    };
}

/// <summary>What the sticker is showing.</summary>
public readonly record struct CalendarContent(
    int Month,
    int Day,
    /// <summary>Sunday is 1.</summary>
    int Weekday,
    WeatherKind Weather,
    DaySlot Slot,
    /// <summary>
    /// Which of the weather icon's three drawings to use. They are meant to be
    /// cycled 1-2-3; they are drawn in register, so only the icon moves.
    /// </summary>
    int Frame = 1)
{
    public static CalendarContent Now(WeatherKind weather, DateTime? at = null)
    {
        var when = at ?? DateTime.Now;
        return new CalendarContent(
            when.Month,
            when.Day,
            (int)when.DayOfWeek + 1,
            weather,
            CalendarVocabulary.SlotForHour(when.Hour));
    }
}
