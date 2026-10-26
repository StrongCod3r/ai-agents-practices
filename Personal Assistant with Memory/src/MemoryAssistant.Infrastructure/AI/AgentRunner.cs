using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Application.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MemoryAssistant.Infrastructure.AI;

public sealed class AgentRunner(IChatClient client, IOptions<AgentOptions> options) : IAgentRunner
{
    public Task<string> RunAsync(
        List<PromptMessage> messages,
        IConversationTools tools,
        List<ToolStep> steps,
        CancellationToken ct
    ) =>
        RunAsync(
            messages.Select(m => new ChatMessage(new ChatRole(m.Role), m.Text)).ToList(),
            [
                AIFunctionFactory.Create(
                    tools.SearchMemory,
                    "SearchMemory",
                    "Busca recuerdos activos por significado."
                ),
                AIFunctionFactory.Create(
                    tools.SaveMemory,
                    "SaveMemory",
                    "Propone guardar un hecho del mensaje actual; requiere aprobación."
                ),
                AIFunctionFactory.Create(
                    tools.UpdateMemory,
                    "UpdateMemory",
                    "Propone corregir un recuerdo identificado; requiere aprobación."
                ),
                AIFunctionFactory.Create(
                    tools.DeleteMemory,
                    "DeleteMemory",
                    "Propone eliminar un recuerdo identificado; pide aclaración si el objetivo es ambiguo."
                ),
                AIFunctionFactory.Create(
                    tools.GetConversationHistory,
                    "GetConversationHistory",
                    "Devuelve los últimos mensajes disponibles de la conversación actual."
                ),
            ],
            steps,
            ct
        );

    public async Task<string> RunAsync(
        List<ChatMessage> messages,
        IList<AITool> tools,
        List<ToolStep> steps,
        CancellationToken ct
    )
    {
        // Agent Framework supplies the agent lifecycle and provider abstraction. This application
        // owns the bounded function loop, so neither framework nor provider can auto-execute tools.
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = "MemoryAssistant",
                UseProvidedChatClientAsIs = true,
                ChatOptions = new()
                {
                    ModelId = options.Value.Model,
                    Tools = tools,
                    MaxOutputTokens = 1400,
                    AllowMultipleToolCalls = false,
                },
            }
        );
        var functions = tools.OfType<AIFunction>().ToDictionary(x => x.Name);
        for (var round = 0; round < options.Value.MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (AgentMessageSizing.Size(messages) > options.Value.ContextCharacters)
                throw new AssistantException(
                    FailureKind.BudgetExceeded,
                    "context_budget",
                    "Se agotó el presupuesto de contexto del agente."
                );
            using var activity = Telemetry.Source.StartActivity("agent.round");
            activity?.SetTag("agent.round", round + 1);
            var response = await agent.RunAsync(messages, cancellationToken: ct);
            var calls = response
                .Messages.SelectMany(x => x.Contents)
                .OfType<FunctionCallContent>()
                .ToArray();
            if (calls.Length == 0)
            {
                var answer = response.Text;
                if (string.IsNullOrWhiteSpace(answer))
                    throw new AssistantException(
                        FailureKind.ProviderFailure,
                        "invalid_provider_response",
                        "El modelo no devolvió respuesta final."
                    );
                return answer;
            }
            if (steps.Count + calls.Length > options.Value.MaxToolCalls)
                throw new AssistantException(
                    FailureKind.BudgetExceeded,
                    "tool_budget",
                    "Se agotó el número de herramientas permitido."
                );
            messages.AddRange(response.Messages);
            foreach (var call in calls)
            {
                object? result;
                try
                {
                    if (!functions.TryGetValue(call.Name, out var function))
                        throw AssistantException.Invalid("Herramienta desconocida.");
                    using var toolActivity = Telemetry.Source.StartActivity("agent.tool");
                    toolActivity?.SetTag("tool.name", function.Name);
                    result = await function.InvokeAsync(
                        new AIFunctionArguments(
                            call.Arguments ?? new Dictionary<string, object?>()
                        ),
                        ct
                    );
                }
                catch (AssistantException ex)
                {
                    result = new
                    {
                        success = false,
                        errorCode = ex.Code,
                        error = ex.Message,
                    };
                }
                catch (MemoryAssistant.Domain.Exceptions.DomainRuleException ex)
                {
                    result = new
                    {
                        success = false,
                        errorCode = "invalid_input",
                        error = ex.Message,
                    };
                }
                catch (Exception ex)
                    when (ex is ArgumentException or System.Text.Json.JsonException)
                {
                    result = new
                    {
                        success = false,
                        errorCode = "invalid_tool_arguments",
                        error = "Argumentos de herramienta inválidos.",
                    };
                }
                steps.Add(new(steps.Count + 1, call.Name, result));
                messages.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
            }
        }
        throw new AssistantException(
            FailureKind.BudgetExceeded,
            "round_budget",
            "Se agotó el número de rondas permitido."
        );
    }
}
