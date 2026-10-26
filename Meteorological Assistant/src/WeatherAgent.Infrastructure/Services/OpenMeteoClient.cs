using WeatherAgent.Domain.Services;
using WeatherAgent.Application.Abstractions;
using System.Globalization;
using System.Text.Json;
using WeatherAgent.Domain.Models;

namespace WeatherAgent.Infrastructure.Services;

// Los hosts son fijos: el modelo solo proporciona nombres y coordenadas, nunca URLs.
public sealed class OpenMeteoClient(HttpClient http, TimeProvider clock) : IWeatherClient
{
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string Units = "&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm&timezone=auto";

    public Task<ToolResult> ResolveLocationAsync(string city, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(city) || city.Trim().Length is < 2 or > 150)
            return Task.FromResult(ToolResult.Fail("invalid_city", "Indica una ciudad de entre 2 y 150 caracteres."));
        return ProtectAsync(async () =>
        {
            using var json = await GetJsonAsync("https://geocoding-api.open-meteo.com/v1/search?name="
                + Uri.EscapeDataString(city.Trim()) + "&count=5&language=es&format=json", token);
            if (!json.RootElement.TryGetProperty("results", out var results))
                return ToolResult.Fail("location_not_found", "No se encontró la ciudad. Prueba con nombre y país.");
            var locations = results.EnumerateArray().Select(item =>
            {
                var latitude = Number(item, "latitude", -90, 90);
                var longitude = Number(item, "longitude", -180, 180);
                var timezone = Text(item, "timezone");
                _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                return new Location(Text(item, "name"), OptionalText(item, "admin1"), OptionalText(item, "country"),
                    OptionalText(item, "country_code"), latitude, longitude, timezone);
            }).ToArray();
            // Geocoding también devuelve prefijos (Madrid Centro, Madridejos...).
            // Prioriza coincidencias exactas sin ocultar homónimos reales.
            var requestedName = city.Split(',')[0].Trim();
            var exact = locations.Where(x => CultureInfo.InvariantCulture.CompareInfo.Compare(x.Name, requestedName,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0).ToArray();
            if (exact.Length > 0) locations = exact;
            return locations.Length == 0
                ? ToolResult.Fail("location_not_found", "No se encontró la ciudad. Prueba con nombre y país.")
                : ToolResult.Ok((IReadOnlyList<Location>)locations);
        }, token);
    }

    public Task<ToolResult> GetCurrentWeatherAsync(double latitude, double longitude, CancellationToken token = default)
    {
        if (!WeatherRules.ValidCoordinates(latitude, longitude)) return InvalidCoordinates();
        return ProtectAsync(async () =>
        {
            using var json = await GetJsonAsync(Coordinates(latitude, longitude)
                + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m" + Units, token);
            var root = json.RootElement;
            var current = root.GetProperty("current");
            var code = Code(current.GetProperty("weather_code"));
            var time = Text(current, "time");
            _ = DateTime.Parse(time, CultureInfo.InvariantCulture);
            return ToolResult.Ok(new CurrentWeather(latitude, longitude, Text(root, "timezone"), time,
                Number(current, "temperature_2m", -100, 70), Number(current, "apparent_temperature", -150, 100),
                Number(current, "relative_humidity_2m", 0, 100), Number(current, "precipitation", 0, 5000),
                Number(current, "wind_speed_10m", 0, 500), code, Conditions(code)));
        }, token);
    }

    public Task<ToolResult> GetForecastAsync(double latitude, double longitude, string date = "today", CancellationToken token = default)
    {
        if (!WeatherRules.ValidCoordinates(latitude, longitude)) return InvalidCoordinates();
        if (!WeatherRules.IsSupportedDate(date))
            return Task.FromResult(ToolResult.Fail("invalid_date", "Usa today, tomorrow o una fecha YYYY-MM-DD."));
        return ProtectAsync(async () =>
        {
            using var json = await GetJsonAsync(Coordinates(latitude, longitude) + Units
                + "&forecast_days=16&daily=temperature_2m_min,temperature_2m_max,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,uv_index_max,weather_code"
                + "&hourly=temperature_2m,apparent_temperature,precipitation_probability,wind_speed_10m,weather_code,is_day", token);
            var root = json.RootElement;
            var timezone = Text(root, "timezone");
            var selection = WeatherRules.SelectDate(date, clock.GetUtcNow(), timezone);
            if (!selection.Success) return ToolResult.Fail("date_out_of_range", selection.Error!);
            var selected = selection.Date;
            var key = selected.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var daily = root.GetProperty("daily");
            var times = daily.GetProperty("time").EnumerateArray().Select(x => x.GetString()).ToArray();
            var index = Array.IndexOf(times, key);
            if (index < 0) return ToolResult.Fail("forecast_unavailable", "El proveedor no devolvió previsión para esa fecha.");
            double Day(string name, double min, double max) => At(daily, name, index, times.Length, min, max);
            var code = Code(ElementAt(daily, "weather_code", index, times.Length));
            var minimum = Day("temperature_2m_min", -100, 70);
            var maximum = Day("temperature_2m_max", -100, 70);
            if (minimum > maximum) throw new InvalidDataException();
            var hourly = root.GetProperty("hourly");
            var hourTimes = hourly.GetProperty("time").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException()).ToArray();
            var hours = new List<HourlyWeather>();
            for (var i = 0; i < hourTimes.Length; i++)
            {
                var localTime = DateTime.Parse(hourTimes[i], CultureInfo.InvariantCulture);
                if (DateOnly.FromDateTime(localTime) != selected) continue;
                double Hour(string name, double min, double max) => At(hourly, name, i, hourTimes.Length, min, max);
                hours.Add(new(hourTimes[i], Hour("temperature_2m", -100, 70), Hour("apparent_temperature", -150, 100),
                    Hour("precipitation_probability", 0, 100), Hour("wind_speed_10m", 0, 500),
                    Code(ElementAt(hourly, "weather_code", i, hourTimes.Length)), Hour("is_day", 0, 1) == 1));
            }
            if (hours.Count == 0) return ToolResult.Fail("forecast_unavailable", "Faltan los datos por horas para esa fecha.");
            return ToolResult.Ok(new ForecastWeather(latitude, longitude, timezone, key, minimum, maximum,
                Day("precipitation_sum", 0, 5000), Day("precipitation_probability_max", 0, 100),
                Day("wind_speed_10m_max", 0, 500), Day("uv_index_max", 0, 50), code, Conditions(code), hours));
        }, token);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            json.Dispose();
            throw new InvalidDataException();
        }
        return json;
    }

    private static async Task<ToolResult> ProtectAsync(Func<Task<ToolResult>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await operation(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return ToolResult.Fail("weather_timeout", "Open-Meteo agotó el tiempo de espera. Reintenta más tarde."); }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? ToolResult.Fail("weather_rate_limit", "Open-Meteo ha limitado las consultas. Espera antes de reintentar.")
                : ToolResult.Fail("weather_unavailable", "No se pudo consultar Open-Meteo. Reintenta más tarde.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or InvalidDataException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        { return ToolResult.Fail("invalid_weather_data", "Open-Meteo devolvió datos incompletos o inválidos; no se han estimado valores ausentes."); }
    }

    private static Task<ToolResult> InvalidCoordinates()
        => Task.FromResult(ToolResult.Fail("invalid_coordinates", "Latitud: -90 a 90; longitud: -180 a 180. Usa coordenadas de ResolveLocation."));
    private static string Coordinates(double latitude, double longitude)
        => ForecastUrl + "?latitude=" + latitude.ToString(CultureInfo.InvariantCulture) + "&longitude=" + longitude.ToString(CultureInfo.InvariantCulture);
    private static string Text(JsonElement item, string name)
        => item.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new InvalidDataException();
    private static string? OptionalText(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static double Number(JsonElement item, string name, double min, double max) => Checked(item.GetProperty(name), min, max);
    private static double Checked(JsonElement value, double min, double max)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= min && number <= max
            ? number : throw new InvalidDataException();
    private static JsonElement ElementAt(JsonElement item, string name, int index, int count)
    {
        var array = item.GetProperty(name);
        if (array.GetArrayLength() != count) throw new InvalidDataException();
        return array[index];
    }
    private static double At(JsonElement item, string name, int index, int count, double min, double max)
        => Checked(ElementAt(item, name, index, count), min, max);
    private static int Code(JsonElement value)
    {
        var code = value.GetInt32();
        if (Conditions(code) == "Desconocido") throw new InvalidDataException();
        return code;
    }
    public static string Conditions(int code) => WeatherRules.Conditions(code);
}
