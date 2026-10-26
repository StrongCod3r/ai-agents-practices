using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartCalculator.Application.Configuration;

namespace SmartCalculator.Infrastructure.Services;

// El SDK OpenAI no transmite extensiones arbitrarias de Semantic Kernel.
// Esta opción específica de OpenRouter se añade en la capa HTTP.
public sealed class OpenRouterReasoningHandler(IOptions<AgentOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true
            && request.Content is not null)
        {
            var original = request.Content;
            var body = JsonNode.Parse(await original.ReadAsStringAsync(cancellationToken))!.AsObject();
            body["reasoning"] = new JsonObject { ["enabled"] = options.Value.ReasoningEnabled };
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            original.Dispose();
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
