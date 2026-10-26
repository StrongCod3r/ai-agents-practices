using WeatherAgent.Domain.Models;
namespace WeatherAgent.Application.Abstractions;

public interface IWeatherClient
{
    Task<ToolResult> ResolveLocationAsync(string city, CancellationToken token = default);
    Task<ToolResult> GetCurrentWeatherAsync(double latitude, double longitude, CancellationToken token = default);
    Task<ToolResult> GetForecastAsync(double latitude, double longitude, string date = "today", CancellationToken token = default);
}
