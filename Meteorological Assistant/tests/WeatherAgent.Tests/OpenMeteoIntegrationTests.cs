using WeatherAgent.Application.Tools;
using WeatherAgent.Domain.Models;
using WeatherAgent.Infrastructure.Services;
using WeatherAgent.Application.Contracts;
using WeatherAgent.Application.Services;

namespace WeatherAgent.Tests;

public class OpenMeteoIntegrationTests
{
    [LiveWeatherFact]
    public async Task QueriesRealCurrentForecastAndTwoCityComparison()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var client = new OpenMeteoClient(http, TimeProvider.System);
        var locations = await client.ResolveLocationAsync("Madrid, España");
        Assert.True(locations.Success, locations.Error);
        var madrid = Assert.IsAssignableFrom<IReadOnlyList<Location>>(locations.Data)[0];
        var current = await client.GetCurrentWeatherAsync(madrid.Latitude, madrid.Longitude);
        Assert.True(current.Success, current.Error);
        var forecast = await client.GetForecastAsync(madrid.Latitude, madrid.Longitude, "tomorrow");
        Assert.True(forecast.Success, forecast.Error);
        Assert.NotEmpty(Assert.IsType<ForecastWeather>(forecast.Data).Hours);
        var comparison = await new WeatherTools(client).CompareWeather(["Madrid, España", "Barcelona, España"], "tomorrow");
        Assert.True(comparison.Success, comparison.Error);
        var data = System.Text.Json.JsonSerializer.SerializeToElement(comparison.Data, System.Text.Json.JsonSerializerOptions.Web);
        Assert.True(data.GetProperty("complete").GetBoolean(), data.ToString());
    }
}

internal sealed class LiveWeatherFactAttribute : FactAttribute
{
    public LiveWeatherFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WEATHER_LIVE_TESTS") != "1")
            Skip = "Opt-in: define WEATHER_LIVE_TESTS=1 para consultar Open-Meteo real (sin OpenRouter).";
    }
}
