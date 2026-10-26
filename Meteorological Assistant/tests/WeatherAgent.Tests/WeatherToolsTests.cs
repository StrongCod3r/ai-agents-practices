using WeatherAgent.Application.Tools;
using WeatherAgent.Domain.Models;
using WeatherAgent.Infrastructure.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using WeatherAgent.Application.Contracts;
using WeatherAgent.Application.Services;

namespace WeatherAgent.Tests;

public class WeatherToolsTests
{
    [Fact]
    public async Task ResolvesCandidatesAndEncodesUserInput()
    {
        string? query = null;
        var client = Create(request => { query = request.RequestUri!.Query; return Json(Locations()); });
        var result = await client.ResolveLocationAsync("Madrid &count=100");
        Assert.True(result.Success);
        Assert.Equal("Madrid", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Location>>(result.Data)).Name);
        Assert.Contains("%26count%3D100", query);
    }

    [Fact]
    public async Task PrefersExactNamesOverPrefixMatches()
    {
        var nodes = System.Text.Json.Nodes.JsonNode.Parse(Locations(2))!;
        nodes["results"]![1]!["name"] = "Madrid Centro";
        var result = await Create(_ => Json(nodes.ToJsonString())).ResolveLocationAsync("Madrid, España");
        Assert.Equal("Madrid", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Location>>(result.Data)).Name);
    }

    [Fact]
    public async Task HandlesNotFoundAndAmbiguityWithoutChoosingFirstCity()
    {
        Assert.Equal("location_not_found", (await Create(_ => Json("{}")).ResolveLocationAsync("Unknown")).ErrorCode);
        var tools = new WeatherTools(Create(_ => Json(Locations(2))));
        var result = await tools.CompareWeather(["Madrid", "Madrid, España"]);
        Assert.False(result.Success);
        Assert.Contains("ambiguous_location", JsonSerializer.Serialize(result.Data, JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task NormalizesCurrentWeatherAndUnits()
    {
        var result = await Create(request =>
        {
            Assert.Contains("wind_speed_unit=kmh", request.RequestUri!.Query);
            return Json(Current());
        }).GetCurrentWeatherAsync(40.4, -3.7);
        var current = Assert.IsType<CurrentWeather>(result.Data);
        Assert.Equal(20, current.TemperatureC);
        Assert.Equal("Despejado", current.Conditions);
    }

    [Fact]
    public async Task NullValuesAreErrorsRatherThanZero()
    {
        var result = await Create(_ => Json(Current().Replace("\"temperature_2m\":20", "\"temperature_2m\":null")))
            .GetCurrentWeatherAsync(40.4, -3.7);
        Assert.False(result.Success);
        Assert.Equal("invalid_weather_data", result.ErrorCode);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"current\":{}}")]
    [InlineData("{\"error\":true,\"reason\":\"private details\"}")]
    public async Task HandlesMalformedProviderData(string body)
    {
        var result = await Create(_ => Json(body)).GetCurrentWeatherAsync(40, -3);
        Assert.Equal("invalid_weather_data", result.ErrorCode);
        Assert.DoesNotContain("private details", result.Error);
    }

    [Fact]
    public async Task UsesDestinationDayAcrossUtcMidnight()
    {
        // 15 Oct 00:30 UTC sigue siendo 14 Oct en Los Ángeles.
        var result = await Create(_ => Json(Forecast("America/Los_Angeles"))).GetForecastAsync(34, -118, "today");
        var forecast = Assert.IsType<ForecastWeather>(result.Data);
        Assert.Equal("2026-10-14", forecast.Date);
        Assert.Single(forecast.Hours);
        var tomorrow = await Create(_ => Json(Forecast("America/Los_Angeles"))).GetForecastAsync(34, -118, "tomorrow");
        Assert.Equal("2026-10-15", Assert.IsType<ForecastWeather>(tomorrow.Data).Date);
    }

    [Fact]
    public async Task RejectsUnavailableDatesAndMisalignedArrays()
    {
        var client = Create(_ => Json(Forecast()));
        Assert.Equal("invalid_date", (await client.GetForecastAsync(40, -3, "15/10/2026")).ErrorCode);
        Assert.Equal("date_out_of_range", (await client.GetForecastAsync(40, -3, "2026-11-01")).ErrorCode);
        var malformed = Forecast().Replace("\"temperature_2m_min\":[10,11]", "\"temperature_2m_min\":[10]");
        Assert.Equal("invalid_weather_data", (await Create(_ => Json(malformed)).GetForecastAsync(40, -3)).ErrorCode);
    }

    [Fact]
    public async Task RejectsBadCoordinatesBeforeHttp()
    {
        var client = Create(_ => throw new Exception("HTTP must not run"));
        Assert.Equal("invalid_coordinates", (await client.GetCurrentWeatherAsync(double.NaN, 0)).ErrorCode);
        Assert.Equal("invalid_coordinates", (await client.GetForecastAsync(91, 0)).ErrorCode);
        Assert.Equal("invalid_city", (await client.ResolveLocationAsync(" ")).ErrorCode);
    }

    [Fact]
    public async Task PreservesSuccessfulCitiesWhenAnotherFails()
    {
        var tools = new WeatherTools(Create(request =>
            request.RequestUri!.Host == "geocoding-api.open-meteo.com"
                ? Json(request.RequestUri.Query.Contains("Missing") ? "{}" : Locations())
                : Json(Forecast())));
        var result = await tools.CompareWeather(["Madrid, España", "Missing"]);
        Assert.True(result.Success);
        var data = JsonSerializer.SerializeToElement(result.Data, JsonSerializerOptions.Web);
        Assert.False(data.GetProperty("complete").GetBoolean());
        Assert.True(data.GetProperty("cities")[0].GetProperty("result").GetProperty("success").GetBoolean());
        Assert.False(data.GetProperty("cities")[1].GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal("invalid_locations", (await tools.CompareWeather(["Madrid"])).ErrorCode);
    }

    [Fact]
    public async Task DistinguishesRateLimitsTimeoutsAndCallerCancellation()
    {
        Assert.Equal("weather_rate_limit", (await Create(_ => new(HttpStatusCode.TooManyRequests)).ResolveLocationAsync("Madrid")).ErrorCode);
        Assert.Equal("weather_timeout", (await Create(_ => throw new TaskCanceledException()).ResolveLocationAsync("Madrid")).ErrorCode);
        Assert.Equal("weather_unavailable", (await Create(_ => throw new HttpRequestException()).ResolveLocationAsync("Madrid")).ErrorCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(_ => Json("{}")).ResolveLocationAsync("Madrid", cancellation.Token));
    }

    internal static OpenMeteoClient Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new StubHandler(respond)), new FixedClock());
    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    internal static string Locations(int count = 1) => JsonSerializer.Serialize(new
    {
        results = Enumerable.Range(0, count).Select(i => new
        {
            name = "Madrid",
            latitude = 40.4 + i,
            longitude = -3.7,
            timezone = "Europe/Madrid",
            country = "España",
            country_code = "ES",
            admin1 = "Madrid"
        })
    });
    internal static string Current() => """
        {"timezone":"Europe/Madrid","current":{"time":"2026-10-15T12:00","temperature_2m":20,"apparent_temperature":19,
        "relative_humidity_2m":50,"precipitation":0,"wind_speed_10m":10,"weather_code":0}}
        """;
    internal static string Forecast(string timezone = "Europe/Madrid") => """
        {"timezone":"TIMEZONE","daily":{"time":["2026-10-14","2026-10-15"],"temperature_2m_min":[10,11],
        "temperature_2m_max":[22,23],"precipitation_sum":[0,0],"precipitation_probability_max":[10,20],
        "wind_speed_10m_max":[15,16],"uv_index_max":[3,4],"weather_code":[0,2]},
        "hourly":{"time":["2026-10-14T10:00","2026-10-15T10:00"],"temperature_2m":[18,19],
        "apparent_temperature":[18,18],"precipitation_probability":[10,20],"wind_speed_10m":[10,11],
        "weather_code":[0,2],"is_day":[1,1]}}
        """.Replace("TIMEZONE", timezone);
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 0, 30, 0, TimeSpan.Zero);
}
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
