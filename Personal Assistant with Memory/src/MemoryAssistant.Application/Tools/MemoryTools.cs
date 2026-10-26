using System.ComponentModel;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Services;

namespace MemoryAssistant.Application.Tools;

public sealed class MemoryTools(
    MemoryService memory,
    IConversationRepository conversations,
    Guid conversationId,
    Guid sourceMessageId
) : IConversationTools
{
    public HashSet<Guid> UsedMemoryIds { get; } = [];

    [Description(
        "Busca recuerdos activos por significado. Los ids sirven para proponer correcciones o borrados."
    )]
    public async Task<SearchResponse> SearchMemory(string query, CancellationToken ct)
    {
        var result = await memory.SearchAsync(query, ct);
        UsedMemoryIds.UnionWith(result.Memories.Select(x => x.Id));
        return result;
    }

    [Description("Propone guardar un hecho del mensaje actual. No aprueba ni activa el recuerdo.")]
    public async Task<MemoryView> SaveMemory(
        string content,
        string category,
        bool sensitive,
        CancellationToken ct
    ) =>
        MemoryService.View(
            await memory.ProposeAsync(
                content,
                category,
                sensitive,
                sourceMessageId,
                null,
                false,
                ct
            )
        );

    [Description(
        "Propone corregir un recuerdo activo identificado sin sobrescribirlo; requiere aprobación del usuario."
    )]
    public async Task<MemoryView> UpdateMemory(
        Guid id,
        string content,
        bool sensitive,
        CancellationToken ct
    )
    {
        var target = await memory.GetAsync(id, ct);
        return MemoryService.View(
            await memory.ProposeAsync(
                content,
                target.Category,
                sensitive,
                sourceMessageId,
                [id],
                false,
                ct
            )
        );
    }

    [Description(
        "Propone eliminar un recuerdo identificado. Pide aclaración si hay varios posibles objetivos. Requiere aprobación por API."
    )]
    public async Task<MemoryView> DeleteMemory(Guid id, CancellationToken ct)
    {
        var target = await memory.GetAsync(id, ct);
        return MemoryService.View(
            await memory.ProposeAsync(
                "Eliminar el recuerdo seleccionado",
                target.Category,
                false,
                sourceMessageId,
                [id],
                true,
                ct
            )
        );
    }

    [Description(
        "Devuelve los últimos mensajes disponibles de la conversación actual, excluyendo los olvidados."
    )]
    public Task<IReadOnlyList<MessageView>> GetConversationHistory(CancellationToken ct) =>
        conversations.GetRecentMessagesAsync(conversationId, 8, ct);
}
