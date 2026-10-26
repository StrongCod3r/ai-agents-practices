using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using Microsoft.Extensions.Configuration;

namespace MemoryAssistant.Infrastructure.Configuration;

public static class OpenRouterConfiguration
{
    public static string? ApiKey(IConfiguration config) =>
        Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is { Length: > 0 } value
            ? value
            : config["OpenRouter:ApiKey"];
}
