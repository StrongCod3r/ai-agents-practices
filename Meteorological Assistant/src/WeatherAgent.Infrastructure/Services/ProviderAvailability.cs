using Microsoft.Extensions.Configuration;
using WeatherAgent.Application.Abstractions;
using WeatherAgent.Infrastructure.Configuration;
namespace WeatherAgent.Infrastructure.Services;

public sealed class ProviderAvailability(IConfiguration configuration) : IProviderAvailability
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(OpenRouterConfiguration.GetApiKey(configuration));
}
