using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using Microsoft.Extensions.Options;

namespace MemoryAssistant.Application.Services;

public sealed class MemoryService(
    IMemoryRepository memories,
    IConversationRepository conversations,
    IUnitOfWork unitOfWork,
    IEmbeddingClient embeddings,
    IOptions<EmbeddingOptions> options
)
{
    public static MemoryView View(MemoryEntry m) =>
        new(
            m.Id,
            m.Content,
            m.Category,
            m.Status.ToString(),
            m.Operation.ToString(),
            m.Sensitive,
            m.Version,
            m.CreatedAt,
            m.UpdatedAt,
            m.Embedding is not null,
            m.Sources.Select(x => x.MessageId).ToArray(),
            m.Targets.Select(x => x.TargetId).ToArray()
        );

    public async Task<List<MemoryView>> ListAsync(string? status, CancellationToken ct)
    {
        MemoryStatus? parsedStatus = null;
        if (status is not null)
        {
            if (
                !Enum.TryParse<MemoryStatus>(status, true, out var parsed)
                || !Enum.IsDefined(parsed)
            )
                throw AssistantException.Invalid("Estado de recuerdo inválido.");
            parsedStatus = parsed;
        }
        return (await memories.ListAsync(parsedStatus, ct)).Select(View).ToList();
    }

    public async Task<MemoryEntry> GetAsync(Guid id, CancellationToken ct) =>
        await memories.GetAsync(id, ct) ?? throw AssistantException.Missing();

    public async Task<MemoryView> GetViewAsync(Guid id, CancellationToken ct) =>
        View(await GetAsync(id, ct));

    public async Task<MemoryChangeResponse> SaveExplicitAsync(
        string content,
        string category,
        bool sensitive,
        bool confirmSensitive,
        CancellationToken ct
    )
    {
        var entry = await ProposeAsync(content, category, sensitive, null, null, false, ct);
        return await CompleteExplicitAsync(entry, confirmSensitive, ct);
    }

    public async Task<MemoryChangeResponse> CorrectExplicitAsync(
        Guid id,
        string content,
        int version,
        string category,
        bool sensitive,
        bool confirmSensitive,
        CancellationToken ct
    )
    {
        CheckVersion(await GetAsync(id, ct), version);
        var candidate = await ProposeAsync(content, category, sensitive, null, [id], false, ct);
        return await CompleteExplicitAsync(candidate, confirmSensitive, ct);
    }

    private async Task<MemoryChangeResponse> CompleteExplicitAsync(
        MemoryEntry entry,
        bool confirmSensitive,
        CancellationToken ct
    )
    {
        if (entry.Status == MemoryStatus.Pending && (!entry.Sensitive || confirmSensitive))
        {
            var result = await ApproveAsync(entry.Id, entry.Version, confirmSensitive, ct);
            return new(result.Memory, result.Warnings);
        }
        return new(View(entry), []);
    }

    public async Task<MemoryEntry> ProposeAsync(
        string content,
        string category,
        bool sensitive,
        Guid? sourceId,
        IReadOnlyList<Guid>? targetIds,
        bool deletion,
        CancellationToken ct
    )
    {
        content = MemoryPolicy.ValidateContent(content);
        MemoryPolicy.ValidateCategory(category);
        var sourceIds = new HashSet<Guid>();
        if (sourceId is { } source)
        {
            var message = await conversations.GetMessageAsync(source, ct);
            if (message is null || message.Excluded || message.Role != "user")
                throw AssistantException.Invalid(
                    "La procedencia debe ser un mensaje de usuario disponible."
                );
            // Do not extract a fragment from a message that contains credentials.
            MemoryPolicy.RejectCredentials(message.Content);
            sourceIds.Add(source);
            sensitive |= MemoryPolicy.IsSensitive(message.Content);
        }
        var targets = new List<MemoryEntry>();
        foreach (var id in (targetIds ?? []).Distinct())
        {
            var target = await GetAsync(id, ct);
            if (target.Status != MemoryStatus.Active)
                throw AssistantException.Conflict(
                    "Solo se pueden sustituir o eliminar recuerdos activos."
                );
            targets.Add(target);
            sourceIds.UnionWith(target.Sources.Select(x => x.MessageId));
            sensitive |= target.Sensitive;
        }
        if (deletion && targets.Count != 1)
            throw AssistantException.Invalid("La eliminación necesita un único recuerdo.");
        var fingerprint = MemoryPolicy.Fingerprint(content);
        if (!deletion)
        {
            var existing = await memories.FindDuplicateAsync(fingerprint, ct);
            if (existing is not null)
            {
                if (
                    targets.Count > 0
                    && existing.Status == MemoryStatus.Active
                    && targets.All(x => x.Id != existing.Id)
                )
                    throw AssistantException.Conflict(
                        "Ya existe ese contenido en otro recuerdo activo; propón una consolidación."
                    );
                if (
                    targets.Count > 0
                    && existing.Status == MemoryStatus.Pending
                    && !targets
                        .Select(x => x.Id)
                        .ToHashSet()
                        .SetEquals(existing.Targets.Select(x => x.TargetId))
                )
                    throw AssistantException.Conflict(
                        "Ya hay una propuesta con ese contenido y otros objetivos."
                    );
                // Preserve all provenance even when an identical fact is repeated.
                foreach (
                    var id in sourceIds.Except(existing.Sources.Select(x => x.MessageId)).ToArray()
                )
                    existing.Sources.Add(new() { MemoryId = existing.Id, MessageId = id });
                if (!existing.Sensitive && (sensitive || MemoryPolicy.IsSensitive(content)))
                {
                    existing.Sensitive = true;
                    Touch(existing);
                }
                await unitOfWork.SaveChangesAsync(ct);
                return existing;
            }
        }
        var entry = new MemoryEntry
        {
            Content = content,
            Category = category,
            Fingerprint = fingerprint,
            Sensitive = sensitive || MemoryPolicy.IsSensitive(content),
            Operation =
                deletion ? MemoryOperation.Delete
                : targets.Count > 0 ? MemoryOperation.Replace
                : MemoryOperation.Create,
        };
        entry.Sources = sourceIds
            .Select(x => new MemorySource { MemoryId = entry.Id, MessageId = x })
            .ToList();
        entry.Targets = targets
            .Select(x => new MemoryTarget
            {
                MemoryId = entry.Id,
                TargetId = x.Id,
                TargetVersion = x.Version,
            })
            .ToList();
        memories.Add(entry);
        await unitOfWork.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<(MemoryView? Memory, List<string> Warnings)> ApproveAsync(
        Guid id,
        int version,
        bool confirmSensitive,
        CancellationToken ct
    )
    {
        var entry = await GetAsync(id, ct);
        CheckVersion(entry, version);
        if (entry.Status != MemoryStatus.Pending)
            throw AssistantException.Conflict("El candidato ya no está pendiente.");
        if (entry.Sensitive && !confirmSensitive && entry.Operation != MemoryOperation.Delete)
            throw new AssistantException(
                FailureKind.Conflict,
                "sensitive_confirmation_required",
                "Confirma expresamente el almacenamiento de datos sensibles."
            );
        var targets = new List<MemoryEntry>();
        foreach (var reference in entry.Targets)
        {
            var target = await GetAsync(reference.TargetId, ct);
            CheckVersion(target, reference.TargetVersion);
            if (target.Status != MemoryStatus.Active)
                throw AssistantException.Conflict(
                    "El recuerdo de origen cambió; crea otra propuesta."
                );
            targets.Add(target);
        }
        if (entry.Operation == MemoryOperation.Delete)
        {
            await ForgetAsync(targets.Single().Id, ct);
            return (null, []);
        }
        var warnings = new List<string>();
        await TryIndexAsync(entry, warnings, ct);
        if (targets.Count > 0)
        {
            await ExcludeTurnsAsync(
                targets.SelectMany(x => x.Sources).Select(x => x.MessageId),
                ct
            );
            foreach (var target in targets)
            {
                target.Status = MemoryStatus.Superseded;
                target.Embedding = null;
                Touch(target);
            }
        }
        entry.Status = MemoryStatus.Active;
        Touch(entry);
        await unitOfWork.SaveChangesAsync(ct);
        return (View(entry), warnings);
    }

    public async Task<MemoryView> RejectAsync(Guid id, int version, CancellationToken ct)
    {
        var entry = await GetAsync(id, ct);
        CheckVersion(entry, version);
        if (entry.Status != MemoryStatus.Pending)
            throw AssistantException.Conflict("El candidato ya no está pendiente.");
        entry.Status = MemoryStatus.Rejected;
        Touch(entry);
        await unitOfWork.SaveChangesAsync(ct);
        return View(entry);
    }

    public async Task<SearchResponse> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 4000)
            throw AssistantException.Invalid("Consulta de 1 a 4000 caracteres requerida.");
        using var activity = Telemetry.Source.StartActivity("memory.search");
        var availability = await memories.GetEmbeddingAvailabilityAsync(
            options.Value.Model,
            options.Value.Dimensions,
            ct
        );
        if (!availability.HasActive)
            return new([], true);
        if (!availability.HasCompatible)
            return new([], false, "Recuerdos pendientes de indexación; ejecuta reindexación.");
        try
        {
            var vector = await embeddings.GenerateAsync(query, ct);
            var hits = await memories.SearchAsync(
                vector,
                options.Value.Model,
                options.Value.Dimensions,
                options.Value.MinSimilarity,
                5,
                ct
            );
            return new(
                hits,
                true,
                availability.HasPending
                    ? "Algunos recuerdos necesitan reindexación y no se incluyeron."
                    : null
            );
        }
        catch (Exception ex) when (ex is AssistantException or HttpRequestException)
        {
            return new(
                [],
                false,
                "Recuperación semántica no disponible; se continúa con el historial."
            );
        }
    }

    public async Task<MaintenanceResponse> ReindexAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var processed = 0;
        foreach (
            var memory in (await memories.ListAsync(MemoryStatus.Active, ct)).OrderBy(x => x.Id)
        )
        {
            var before = warnings.Count;
            await TryIndexAsync(memory, warnings, ct);
            if (before == warnings.Count)
                processed++;
            await unitOfWork.SaveChangesAsync(ct);
        }
        return new(processed, warnings.Distinct().ToArray());
    }

    public async Task ForgetAsync(Guid id, CancellationToken ct)
    {
        _ = await GetAsync(id, ct);
        var all = await memories.ListAsync(null, ct);
        var remove = new HashSet<Guid> { id };
        var sourceIds = new HashSet<Guid>();
        bool changed;
        do
        {
            var previousCount = remove.Count;
            foreach (var memory in all.Where(x => remove.Contains(x.Id)))
            {
                sourceIds.UnionWith(memory.Sources.Select(x => x.MessageId));
                remove.UnionWith(memory.Targets.Select(x => x.TargetId));
            }
            foreach (
                var memory in all.Where(x =>
                    x.Sources.Any(s => sourceIds.Contains(s.MessageId))
                    || x.Targets.Any(t => remove.Contains(t.TargetId))
                )
            )
                remove.Add(memory.Id);
            changed = previousCount != remove.Count;
        } while (changed);
        await ExcludeTurnsAsync(sourceIds, ct);
        memories.RemoveRange(all.Where(x => remove.Contains(x.Id)));
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task ExcludeTurnsAsync(IEnumerable<Guid> sources, CancellationToken ct)
    {
        var ids = sources.Distinct().ToArray();
        var messages = await conversations.GetMessagesInSourceTurnsAsync(ids, ct);
        foreach (var message in messages)
            message.Excluded = true;
        foreach (var conversationId in messages.Select(x => x.ConversationId).Distinct())
        {
            var conversation = await conversations.GetAsync(conversationId, ct);
            if (conversation is null)
                continue;
            conversation.Summary = null;
            conversation.SummaryThroughSequence = 0;
        }
    }

    public async Task RecordUsageAsync(
        IEnumerable<Guid> memoryIds,
        Guid messageId,
        CancellationToken ct
    )
    {
        foreach (var id in memoryIds.Distinct())
        {
            var memory = await memories.GetAsync(id, ct);
            if (
                memory?.Status == MemoryStatus.Active
                && memory.Sources.All(x => x.MessageId != messageId)
            )
                memory.Sources.Add(new() { MemoryId = id, MessageId = messageId });
        }
        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task TryIndexAsync(MemoryEntry entry, List<string> warnings, CancellationToken ct)
    {
        try
        {
            entry.Embedding = await embeddings.GenerateAsync(entry.Content, ct);
            entry.EmbeddingModel = options.Value.Model;
            entry.EmbeddingDimensions = options.Value.Dimensions;
        }
        catch (Exception ex) when (ex is AssistantException or HttpRequestException)
        {
            warnings.Add("No se pudo indexar un recuerdo; permanece pendiente de indexación.");
        }
    }

    public static void CheckVersion(MemoryEntry entry, int version)
    {
        if (entry.Version != version)
            throw AssistantException.Conflict(
                "La versión del recuerdo cambió; vuelve a consultarlo."
            );
    }

    private static void Touch(MemoryEntry entry)
    {
        entry.Version++;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
