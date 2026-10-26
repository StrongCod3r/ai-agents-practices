using System.ComponentModel;
using Microsoft.SemanticKernel;
using WeatherAgent.Domain.Models;
using WeatherAgent.Application.Tools;

namespace WeatherAgent.Infrastructure.Tools;

public sealed class WeatherPlugin(WeatherTools tools)
{
    [KernelFunction, Description("Busca candidatos de una ciudad. Si hay varios, pide aclaración; no adivines coordenadas. Incluye país o región en city para acotar.")]
    public Task<ToolResult> ResolveLocation([Description("Ciudad, opcionalmente seguida de país o región: Madrid, España")] string city,
        CancellationToken cancellationToken = default) => tools.ResolveLocation(city, cancellationToken);

    [KernelFunction, Description("Obtiene condiciones actuales estimadas en coordenadas resueltas: °C, km/h, mm, hora local y zona horaria.")]
    public Task<ToolResult> GetCurrentWeather(double latitude, double longitude, CancellationToken cancellationToken = default)
        => tools.GetCurrentWeather(latitude, longitude, cancellationToken);

    [KernelFunction, Description("Previsión diaria y por horas para coordenadas resueltas. Admite hoy y los 15 días siguientes, según el día local del destino.")]
    public Task<ToolResult> GetWeatherForecast(double latitude, double longitude,
        [Description("today, tomorrow o fecha ISO YYYY-MM-DD; por defecto today")] string date = "today",
        CancellationToken cancellationToken = default) => tools.GetWeatherForecast(latitude, longitude, date, cancellationToken);

    [KernelFunction, Description("Compara previsiones de 2 a 5 ciudades, conservando errores por ciudad. Usa nombres con país para evitar ambigüedad. No ordena ni declara ganador; interpreta los datos para la actividad.")]
    public Task<ToolResult> CompareWeather(string[] locations, string date = "today", CancellationToken cancellationToken = default)
        => tools.CompareWeather(locations, date, cancellationToken);
}
