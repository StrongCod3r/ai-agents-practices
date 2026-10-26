using WeatherAgent.Domain.Models;
using WeatherAgent.Application.Abstractions;

namespace WeatherAgent.Application.Tools;

public sealed class WeatherTools(IWeatherClient client)
{
    public Task<ToolResult> ResolveLocation(string city,
        CancellationToken cancellationToken = default) => client.ResolveLocationAsync(city, cancellationToken);

    public Task<ToolResult> GetCurrentWeather(double latitude, double longitude, CancellationToken cancellationToken = default)
        => client.GetCurrentWeatherAsync(latitude, longitude, cancellationToken);

    public Task<ToolResult> GetWeatherForecast(double latitude, double longitude,
        string date = "today",
        CancellationToken cancellationToken = default) => client.GetForecastAsync(latitude, longitude, date, cancellationToken);

    public async Task<ToolResult> CompareWeather(string[] locations, string date = "today", CancellationToken cancellationToken = default)
    {
        if (locations is null || locations.Length is < 2 or > 5 || locations.Any(string.IsNullOrWhiteSpace))
            return ToolResult.Fail("invalid_locations", "Indica entre 2 y 5 ciudades no vacías.");
        var results = await Task.WhenAll(locations.Select(async city =>
        {
            var resolved = await client.ResolveLocationAsync(city, cancellationToken);
            if (!resolved.Success) return new CityForecast(city, null, resolved);
            var candidates = (IReadOnlyList<Location>)resolved.Data!;
            if (candidates.Count != 1)
                return new CityForecast(city, null, ToolResult.Fail("ambiguous_location",
                    "Hay varias ubicaciones. Concreta país o región.", candidates));
            var location = candidates[0];
            return new CityForecast(city, location, await client.GetForecastAsync(location.Latitude, location.Longitude, date, cancellationToken));
        }));
        var data = new { date, cities = results, complete = results.All(x => x.Result.Success), source = "Open-Meteo / GeoNames" };
        return results.Any(x => x.Result.Success) ? ToolResult.Ok(data)
            : ToolResult.Fail("comparison_failed", "No se pudo obtener ninguna previsión. Revisa los errores por ciudad.", data);
    }
}
