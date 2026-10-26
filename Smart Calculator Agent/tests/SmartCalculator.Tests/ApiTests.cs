using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SmartCalculator.Application.Contracts;

namespace SmartCalculator.Tests;

public class ApiTests
{
    [Fact]
    public async Task ExposesHealthOpenApiAndScalarWithoutKey()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var openApi = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/calculator", openApi);
        Assert.Contains("CalculationStep", openApi);
        Assert.Contains("scalar", await client.GetStringAsync("/scalar/v1"), StringComparison.OrdinalIgnoreCase);
        var response = await client.PostAsJsonAsync("/api/calculator", new { message = "Suma 1 y 2" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task RejectsEmptyInput(string? message)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/calculator", new { message })).StatusCode);
    }

    [Fact]
    public async Task RejectsOverlongInput()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/calculator", new { message = new string('a', 4001) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TranslatesExpiredDeadlineToGatewayTimeout()
    {
        await using var factory = Factory("test-key", new ScriptedChat((_, _) => throw new OperationCanceledException()));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/calculator", new { message = "Suma 1 y 2" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsStructuredResponseThroughHttp()
    {
        await using var factory = Factory("test-key", new ScriptedChat(
            (_, _) =>
            {
                var reply = new ChatMessageContent(AuthorRole.Assistant, content: null);
                reply.Items.Add(new FunctionCallContent("Add", "Calculator", "first", new() { ["a"] = 1, ["b"] = 2 }));
                return reply;
            },
            (_, _) => new(AuthorRole.Assistant, "3")));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/calculator", new { message = "Suma 1 y 2" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CalculateResponse>();
        Assert.Equal(3, Assert.Single(result!.Steps).Result.Value);
    }

    [Fact]
    public async Task ReportsUnavailableModelWithProviderStatus()
    {
        await using var factory = Factory("test-key", new ScriptedChat((_, _) =>
            throw new HttpOperationException("private provider response") { StatusCode = HttpStatusCode.NotFound }));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/calculator", new { message = "Suma 1 y 2" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("model_unavailable", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(404, body.RootElement.GetProperty("providerStatus").GetInt32());
        Assert.Contains("Agent__Model", body.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("private provider response", body.RootElement.ToString());
    }

    [Fact]
    public async Task TranslatesProviderFailureToBadGateway()
    {
        await using var factory = Factory("test-key", new ScriptedChat((_, _) => throw new HttpRequestException("private provider details")));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/calculator", new { message = "Suma 1 y 2" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("private provider details", await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> Factory(string key = "", IChatCompletionService? chat = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = key
            }));
            if (chat is not null)
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IChatCompletionService>();
                    services.AddSingleton(chat);
                });
        });
}
