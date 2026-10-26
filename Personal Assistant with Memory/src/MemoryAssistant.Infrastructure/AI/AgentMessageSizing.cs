using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MemoryAssistant.Infrastructure.AI;

internal static class AgentMessageSizing
{
    public static int Size(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m =>
            m.Contents.Sum(c =>
                c switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    FunctionCallContent call => call.Name.Length
                        + JsonSerializer.Serialize(call.Arguments).Length,
                    FunctionResultContent result => JsonSerializer.Serialize(result.Result).Length,
                    _ => 0,
                }
            ) + 32
        );
}
