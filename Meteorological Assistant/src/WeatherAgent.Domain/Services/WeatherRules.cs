using System.Globalization;
namespace WeatherAgent.Domain.Services;

public sealed record ForecastDateSelection(bool Success, DateOnly Date, string? Error);
public static class WeatherRules
{
    public static bool ValidCoordinates(double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    public static bool IsSupportedDate(string date)
        => date is "today" or "tomorrow" || DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static ForecastDateSelection SelectDate(string date, DateTimeOffset utcNow, string timezone)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timezone));
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var selected = date switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            _ => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        if (selected < today || selected > today.AddDays(15))
            return new(false, selected, $"La fecha debe estar entre {today:yyyy-MM-dd} y {today.AddDays(15):yyyy-MM-dd} en {timezone}.");
        return new(true, selected, null);
    }
    public static string Conditions(int code) => code switch
    {
        0 => "Despejado",
        1 => "Principalmente despejado",
        2 => "Parcialmente nublado",
        3 => "Cubierto",
        45 or 48 => "Niebla",
        51 or 53 or 55 => "Llovizna",
        56 or 57 => "Llovizna helada",
        61 or 63 or 65 => "Lluvia",
        66 or 67 => "Lluvia helada",
        71 or 73 or 75 or 77 => "Nieve",
        80 or 81 or 82 => "Chubascos",
        85 or 86 => "Chubascos de nieve",
        95 => "Tormenta",
        96 or 99 => "Tormenta con granizo",
        _ => "Desconocido"
    };
}
