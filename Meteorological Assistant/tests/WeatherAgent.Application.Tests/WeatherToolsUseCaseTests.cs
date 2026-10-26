using System.Text.Json;
using WeatherAgent.Application.Abstractions;
using WeatherAgent.Application.Tools;
using WeatherAgent.Domain.Models;
using WeatherAgent.Domain.Services;

namespace WeatherAgent.Application.Tests;

public sealed class WeatherToolsUseCaseTests
{
    [Fact]
    public async Task AmbiguousLocationsNeverTriggerForecastCalls()
    {
        var client = new FakeWeatherClient();
        var result = await new WeatherTools(client).CompareWeather(["ambigua", "Madrid"]);
        Assert.True(result.Success);
        Assert.Equal(new[] { 40d }, client.ForecastLatitudes);
        var data = JsonSerializer.SerializeToElement(result.Data, JsonSerializerOptions.Web);
        Assert.False(data.GetProperty("complete").GetBoolean());
        Assert.Equal("ambiguous_location", data.GetProperty("cities")[0].GetProperty("result").GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task CompleteFailureRetainsIndividualErrors()
    {
        var client = new FakeWeatherClient();
        var result = await new WeatherTools(client).CompareWeather(["desconocida", "ambigua"]);
        Assert.False(result.Success);
        Assert.Equal("comparison_failed", result.ErrorCode);
        Assert.Empty(client.ForecastLatitudes);
        var data = JsonSerializer.SerializeToElement(result.Data, JsonSerializerOptions.Web);
        Assert.Equal(2, data.GetProperty("cities").GetArrayLength());
    }

    [Fact]
    public async Task InvalidComparisonDoesNotReachClient()
    {
        var result = await new WeatherTools(new FakeWeatherClient()).CompareWeather(["Madrid"]);
        Assert.Equal("invalid_locations", result.ErrorCode);
    }

    [Fact]
    public void RelativeDatesUseDestinationDayAndEnforceForecastHorizon()
    {
        var utc = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
        var local = WeatherRules.SelectDate("today", utc, "Europe/Madrid");
        Assert.Equal(new DateOnly(2026, 10, 20), local.Date);
        Assert.True(local.Success);
        Assert.True(WeatherRules.SelectDate("2026-11-04", utc, "Europe/Madrid").Success);
        Assert.False(WeatherRules.SelectDate("2026-11-05", utc, "Europe/Madrid").Success);
        Assert.False(WeatherRules.SelectDate("2026-10-19", utc, "Europe/Madrid").Success);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(91, 0)]
    [InlineData(0, -181)]
    public void InvalidCoordinatesAreRejectedByDomain(double latitude, double longitude)
        => Assert.False(WeatherRules.ValidCoordinates(latitude, longitude));

    private sealed class FakeWeatherClient : IWeatherClient
    {
        public List<double> ForecastLatitudes { get; } = [];
        public Task<ToolResult> ResolveLocationAsync(string city, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var madrid = new Location("Madrid", null, "España", "ES", 40, -3, "Europe/Madrid");
            return Task.FromResult(city switch
            {
                "Madrid" => ToolResult.Ok((IReadOnlyList<Location>)[madrid]),
                "ambigua" => ToolResult.Ok((IReadOnlyList<Location>)[madrid, madrid with { Latitude = 41 }]),
                _ => ToolResult.Fail("location_not_found", "Sin resultados")
            });
        }
        public Task<ToolResult> GetCurrentWeatherAsync(double latitude, double longitude, CancellationToken token = default)
            => throw new NotSupportedException();
        public Task<ToolResult> GetForecastAsync(double latitude, double longitude, string date = "today", CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            ForecastLatitudes.Add(latitude);
            return Task.FromResult(ToolResult.Ok(new { date, temperatureC = 20 }));
        }
    }
}
