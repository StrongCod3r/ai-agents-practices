using Microsoft.Extensions.Configuration;
namespace WeatherAgent.Infrastructure.Configuration;

public static class OpenRouterConfiguration
{
    public static string? GetApiKey(IConfiguration configuration)
    {
        var key = configuration["OPENROUTER_API_KEY"];
        if (string.IsNullOrWhiteSpace(key)) key = configuration["OpenRouter:ApiKey"];
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}

