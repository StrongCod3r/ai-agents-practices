using SmartCalculator.Infrastructure.Services;
using SmartCalculator.Infrastructure.Configuration;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using SmartCalculator.Application.Configuration;
using SmartCalculator.Application.Services;

namespace SmartCalculator.Tests;

public class ProviderFailureTests
{
    [Theory]
    [InlineData(401, "provider_authentication")]
    [InlineData(403, "provider_authentication")]
    [InlineData(402, "provider_credits")]
    [InlineData(404, "model_unavailable")]
    [InlineData(429, "provider_rate_limit")]
    [InlineData(400, "provider_request_rejected")]
    [InlineData(503, "provider_unavailable")]
    public void ClassifiesWrappedProviderErrorsWithoutExposingResponse(int status, string code)
    {
        var provider = new HttpOperationException("secret-provider-message")
        {
            StatusCode = (HttpStatusCode)status,
            ResponseContent = "secret-provider-body"
        };
        var result = ProviderFailureClassifier.FromException(new Exception("wrapper", provider));
        Assert.Equal(status, result.ProviderStatus);
        Assert.Equal(code, result.Code);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public void ClassifiesNetworkFailure()
    {
        var result = ProviderFailureClassifier.FromException(new HttpRequestException("secret details"));
        Assert.Equal("provider_connection", result.Code);
        Assert.Null(result.ProviderStatus);
    }

    [Theory]
    [InlineData("", "fallback")]
    [InlineData("  primary  ", "primary")]
    public void ResolvesSameKeyForValidationAndConnector(string environmentKey, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = environmentKey,
            ["OpenRouter:ApiKey"] = "fallback"
        }).Build();
        Assert.Equal(expected, OpenRouterConfiguration.GetApiKey(config));
    }
}
