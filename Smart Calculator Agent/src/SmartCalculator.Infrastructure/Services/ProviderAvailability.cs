using Microsoft.Extensions.Configuration;
using SmartCalculator.Application.Abstractions;
using SmartCalculator.Infrastructure.Configuration;
namespace SmartCalculator.Infrastructure.Services;

public sealed class ProviderAvailability(IConfiguration configuration) : IProviderAvailability
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(OpenRouterConfiguration.GetApiKey(configuration));
}
