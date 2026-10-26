using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using WeatherAgent.Application.Contracts;

namespace WeatherAgent.Tests;

public class ApiTests
{
    [Fact]
    public async Task ExposesHealthOpenApiScalarAndConfigurationError()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Contains("/api/weather", await client.GetStringAsync("/openapi/v1.json"));
        Assert.Contains("scalar", await client.GetStringAsync("/scalar/v1"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/weather", new { message = "Tiempo en Madrid" })).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RejectsEmptyMessage(string? message)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/weather", new { message })).StatusCode);
    }

    [Fact]
    public async Task RejectsOversizedMessageAndMalformedJson()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/weather", new { message = new string('x', 4001) })).StatusCode);
        using var content = new StringContent("{bad", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/weather", content)).StatusCode);
    }

    [Fact]
    public async Task ReturnsAnswerAndTraceThroughHttp()
    {
        await using var factory = Factory("test-key", new ScriptedChat(
            (_, _) => WeatherAgentTests.Call("GetCurrentWeather", new() { ["latitude"] = 100, ["longitude"] = 0 }),
            (_, _) => new(AuthorRole.Assistant, "La latitud no es válida.")));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/weather", new { message = "Tiempo" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<WeatherResponse>();
        Assert.Equal("invalid_coordinates", Assert.Single(result!.Steps).Result.ErrorCode);
    }

    [Theory]
    [InlineData(401, "provider_authentication")]
    [InlineData(402, "provider_credits")]
    [InlineData(404, "model_unavailable")]
    [InlineData(429, "provider_rate_limit")]
    public async Task ReportsProviderFailuresWithoutLeakingDetails(int status, string errorCode)
    {
        await using var factory = Factory("test-key", new ScriptedChat((_, _) =>
            throw new HttpOperationException("private provider response") { StatusCode = (HttpStatusCode)status }));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/weather", new { message = "Tiempo" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(errorCode, body);
        Assert.DoesNotContain("private provider response", body);
    }

    [Fact]
    public async Task TranslatesDeadlineTo504()
    {
        await using var factory = Factory("test-key", new ScriptedChat((_, _) => throw new OperationCanceledException()));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await client.PostAsJsonAsync("/api/weather", new { message = "Tiempo" })).StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(string key = "", IChatCompletionService? chat = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = key,
                ["OpenRouter:ApiKey"] = ""
            }));
            if (chat is not null) builder.ConfigureServices(services =>
            {
                services.RemoveAll<IChatCompletionService>();
                services.AddSingleton(chat);
            });
        });
}
