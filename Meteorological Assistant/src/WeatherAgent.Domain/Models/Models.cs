namespace WeatherAgent.Domain.Models;

public sealed record ToolResult(bool Success, object? Data, string? ErrorCode, string? Error)
{
    public static ToolResult Ok(object data) => new(true, data, null, null);
    public static ToolResult Fail(string code, string error, object? data = null) => new(false, data, code, error);
}

public sealed record Location(string Name, string? Region, string? Country, string? CountryCode,
    double Latitude, double Longitude, string Timezone);
public sealed record CurrentWeather(double Latitude, double Longitude, string Timezone, string LocalTime,
    double TemperatureC, double ApparentTemperatureC, double HumidityPercent,
    double PrecipitationMm, double WindKmh, int WeatherCode, string Conditions);
public sealed record HourlyWeather(string LocalTime, double TemperatureC, double ApparentTemperatureC,
    double PrecipitationProbabilityPercent, double WindKmh, int WeatherCode, bool IsDay);
public sealed record ForecastWeather(double Latitude, double Longitude, string Timezone, string Date,
    double MinimumTemperatureC, double MaximumTemperatureC, double PrecipitationMm,
    double PrecipitationProbabilityPercent, double MaximumWindKmh, double MaximumUvIndex,
    int WeatherCode, string Conditions, IReadOnlyList<HourlyWeather> Hours);
public sealed record CityForecast(string Query, Location? Location, ToolResult Result);
