using System.Net.Http.Json;
using System.Text.Json;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Exceptions;
using Microsoft.Extensions.Options;

namespace MemoryAssistant.Infrastructure.AI;

public sealed class OllamaEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> options)
    : IEmbeddingClient
{
    public async Task<float[]> GenerateAsync(string text, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var endpoint = new Uri(new Uri(options.Value.BaseUrl.TrimEnd('/') + "/"), "api/embed");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = JsonContent.Create(
            new
            {
                model = options.Value.Model,
                input = text,
                dimensions = options.Value.Dimensions,
                truncate = false,
            }
        );
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new AssistantException(
                    FailureKind.ProviderFailure,
                    "embedding_provider",
                    $"Ollama respondió HTTP {(int)response.StatusCode}. Comprueba que el modelo esté descargado."
                );
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token
            );
            var embeddings = json.RootElement.GetProperty("embeddings");
            if (embeddings.GetArrayLength() != 1)
                throw new AssistantException(
                    FailureKind.ProviderFailure,
                    "invalid_embedding",
                    "Ollama debe devolver exactamente un vector."
                );
            var vector = embeddings[0].EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (
                vector.Length != options.Value.Dimensions
                || vector.Any(x => !float.IsFinite(x))
                || vector.All(x => x == 0)
            )
                throw new AssistantException(
                    FailureKind.ProviderFailure,
                    "invalid_embedding",
                    "El proveedor devolvió un vector inválido o de dimensión distinta."
                );
            return vector;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException(
                FailureKind.Timeout,
                "embedding_timeout",
                "Se agotó el tiempo de embeddings."
            );
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or IndexOutOfRangeException
            )
        {
            throw new AssistantException(
                FailureKind.ProviderFailure,
                "invalid_embedding",
                "La respuesta de embeddings no es válida."
            );
        }
        catch (HttpRequestException)
        {
            throw new AssistantException(
                FailureKind.ProviderFailure,
                "embedding_connection",
                "No se pudo conectar con Ollama. Comprueba que esté iniciado y revisa Embeddings:BaseUrl."
            );
        }
    }
}
