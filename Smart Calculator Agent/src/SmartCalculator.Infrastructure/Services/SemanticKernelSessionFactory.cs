using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using SmartCalculator.Application.Abstractions;
using SmartCalculator.Domain.Models;
using SmartCalculator.Infrastructure.Tools;
namespace SmartCalculator.Infrastructure.Services;

public sealed class SemanticKernelSessionFactory(IChatCompletionService chat) : IAgentSessionFactory
{
    public IAgentSession Create(string instructions, string message) => new Session(chat, new CalculatorPlugin(), instructions, message);

    private sealed class Session : IAgentSession
    {
        private readonly IChatCompletionService chat;
        private readonly Kernel kernel = new();
        private readonly ChatHistory history;
        private readonly Dictionary<AgentToolCall, FunctionCallContent> nativeCalls = new(ReferenceEqualityComparer.Instance);
        public Session(IChatCompletionService chat, CalculatorPlugin plugin, string instructions, string message)
        {
            this.chat = chat;
            kernel.Plugins.AddFromObject(plugin, "Calculator");
            history = new ChatHistory(instructions);
            history.AddUserMessage(message);
        }
        public async Task<AgentReply> GetReplyAsync(CancellationToken cancellationToken)
        {
            var reply = await chat.GetChatMessageContentAsync(history,
                new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false) }, kernel, cancellationToken);
            nativeCalls.Clear();
            var calls = FunctionCallContent.GetFunctionCalls(reply).Select(call =>
            {
                var mapped = new AgentToolCall(call.Id, call.PluginName, call.FunctionName,
                    JsonSerializer.SerializeToElement(call.Arguments ?? new KernelArguments()));
                nativeCalls.Add(mapped, call);
                return mapped;
            }).ToArray();
            if (calls.Length > 0) history.Add(reply);
            return new(reply.Content, calls);
        }
        public async Task<ToolResult> InvokeAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            try
            {
                return (await nativeCalls[call].InvokeAsync(kernel, cancellationToken)).Result as ToolResult
                    ?? throw new InvalidOperationException("Resultado inesperado de herramienta.");
            }
            catch (Exception ex) when (ex is KernelException or ArgumentException or JsonException or KeyNotFoundException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ToolResult.Fail("Herramienta o argumentos inválidos. Usa las funciones disponibles y parámetros numéricos requeridos.");
            }
        }
        public void RecordResult(AgentToolCall call, ToolResult result)
            => history.Add(new FunctionResultContent(nativeCalls[call], JsonSerializer.Serialize(result, JsonSerializerOptions.Web)).ToChatMessage());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
