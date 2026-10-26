using System.Text.Json;
using WeatherAgent.Domain.Models;
using WeatherAgent.Application.Services;
namespace WeatherAgent.Application.Abstractions;

public sealed record AgentToolCall(string? Id, string? PluginName, string FunctionName, JsonElement Arguments);
public sealed record AgentReply(string? Content, IReadOnlyList<AgentToolCall> Calls);
public interface IAgentSessionFactory { IAgentSession Create(string instructions, string message); }
public interface IAgentSession : IAsyncDisposable
{
    Task<AgentReply> GetReplyAsync(CancellationToken cancellationToken);
    Task<ToolResult> InvokeAsync(AgentToolCall call, CancellationToken cancellationToken);
    void RecordResult(AgentToolCall call, ToolResult result);
}
public interface IProviderAvailability { bool IsConfigured { get; } }
public interface IProviderFailureClassifier { ProviderFailure Classify(Exception exception); }
