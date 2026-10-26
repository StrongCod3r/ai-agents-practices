using System.Text.RegularExpressions;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Application.Tools;
using MemoryAssistant.Domain.Entities;
using Microsoft.Extensions.Options;

namespace MemoryAssistant.Application.Services;

public sealed partial class ConversationService(
    IConversationRepository conversations,
    IMemoryRepository memories,
    IUnitOfWork unitOfWork,
    MemoryService memory,
    IAgentRunner runner,
    IMemoryProcessor processor,
    ContextBuilder context,
    IOptions<AgentOptions> options
)
{
    public async Task<ConversationView> CreateAsync(CancellationToken ct)
    {
        var conversation = new Conversation();
        conversations.Add(conversation);
        await unitOfWork.SaveChangesAsync(ct);
        return View(conversation);
    }

    public async Task<ConversationView> GetAsync(Guid id, CancellationToken ct) =>
        View(await FindAsync(id, ct));

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct) =>
        conversations.ListAsync(100, ct);

    private async Task<Conversation> FindAsync(Guid id, CancellationToken ct) =>
        await conversations.GetAsync(id, ct) ?? throw AssistantException.Missing();

    private static ConversationView View(Conversation c) =>
        new(
            c.Id,
            c.CreatedAt,
            c.Summary,
            c.Messages.OrderBy(x => x.Sequence)
                .Select(x => new MessageView(
                    x.Id,
                    x.Sequence,
                    x.Role,
                    x.Excluded ? "[Excluido por olvido o corrección]" : x.Content,
                    x.Excluded,
                    x.CreatedAt
                ))
                .ToArray()
        );

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var conversation = await FindAsync(id, ct);
        // Delete derived memories first, including their use in other conversations.
        var ids = conversation.Messages.Select(x => x.Id).ToArray();
        var related = await memories.FindRelatedToMessagesAsync(ids, ct);
        foreach (var memoryId in related)
            if (await memories.ExistsAsync(memoryId, ct))
                await memory.ForgetAsync(memoryId, ct);
        conversations.Remove(conversation);
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<MessageResponse> SendAsync(Guid id, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            throw AssistantException.Invalid("El mensaje debe contener entre 1 y 4000 caracteres.");
        if (string.IsNullOrWhiteSpace(options.Value.Model))
            throw new AssistantException(
                FailureKind.Unavailable,
                "provider_configuration",
                "Configura Agent:Model con un modelo que admita herramientas."
            );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            return await SendCoreAsync(id, text.Trim(), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException(
                FailureKind.Timeout,
                "agent_timeout",
                "Se agotó el tiempo total del turno."
            );
        }
    }

    private async Task<MessageResponse> SendCoreAsync(Guid id, string text, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("conversation.turn");
        var conversation = await FindAsync(id, ct);
        var nextSequence =
            conversation.Messages.Count == 0 ? 1 : conversation.Messages.Max(x => x.Sequence) + 1;
        var userMessage = new ConversationMessage
        {
            ConversationId = id,
            Content = text,
            Sequence = nextSequence,
            TurnId = Guid.NewGuid(),
        };
        conversation.Messages.Add(userMessage);
        conversations.AddMessage(userMessage);
        await unitOfWork.SaveChangesAsync(ct);
        var warnings = new List<string>();
        var steps = new List<ToolStep>();
        var explicitCommand = await ExecuteExplicitAsync(text, userMessage.Id, warnings, ct);
        var search = await memory.SearchAsync(text, ct);
        if (search.Warning is not null)
            warnings.Add(search.Warning);
        var history = conversation.Messages.OrderBy(x => x.Sequence).ToArray();
        var messages = context.Build(conversation, history, search.Memories, explicitCommand);
        var tools = new MemoryTools(memory, conversations, id, userMessage.Id);
        var answer = await runner.RunAsync(messages, tools, steps, ct);
        var assistantMessage = new ConversationMessage
        {
            ConversationId = id,
            TurnId = userMessage.TurnId,
            Sequence = nextSequence + 1,
            Role = "assistant",
            Content = answer,
        };
        conversation.Messages.Add(assistantMessage);
        conversations.AddMessage(assistantMessage);
        await unitOfWork.SaveChangesAsync(ct);
        tools.UsedMemoryIds.UnionWith(search.Memories.Select(x => x.Id));
        await memory.RecordUsageAsync(tools.UsedMemoryIds, userMessage.Id, ct);
        await memory.RecordUsageAsync(tools.UsedMemoryIds, assistantMessage.Id, ct);

        // Maintenance runs inside the turn deadline but its failures must not discard an answer.
        if (explicitCommand is null && !userMessage.Excluded)
        {
            try
            {
                var active = (await memory.ListAsync("Active", ct)).Take(30).ToArray();
                var activeIds = active.Select(x => x.Id).ToHashSet();
                var drafts = await processor.ExtractAsync(text, active, ct);
                foreach (var draft in drafts)
                {
                    if (
                        string.IsNullOrWhiteSpace(draft.Evidence)
                        || !text.Contains(draft.Evidence, StringComparison.OrdinalIgnoreCase)
                    )
                        continue;
                    var targets = draft.Replaces?.Distinct().ToArray() ?? [];
                    if (targets.Length > 5 || targets.Any(x => !activeIds.Contains(x)))
                        continue;
                    await memory.ProposeAsync(
                        draft.Content,
                        draft.Category,
                        draft.Sensitive,
                        userMessage.Id,
                        targets,
                        false,
                        ct
                    );
                }
            }
            catch (Exception ex) when (IsMaintenanceFailure(ex))
            {
                warnings.Add(
                    "La respuesta se conservó, pero la extracción de recuerdos no se completó. Consulta los candidatos disponibles."
                );
            }
        }
        try
        {
            await SummarizeAsync(conversation, ct);
        }
        catch (Exception ex) when (IsMaintenanceFailure(ex))
        {
            warnings.Add("La respuesta se conservó, pero no se pudo actualizar el resumen.");
        }

        // A short cancelled maintenance budget must still permit returning the already committed response.
        var candidates = (await memory.ListAsync("Pending", CancellationToken.None))
            .Where(x => x.SourceMessageIds.Contains(userMessage.Id))
            .ToArray();
        return new(
            id,
            answer,
            options.Value.Model,
            search.Memories,
            candidates,
            steps,
            warnings.Distinct().ToArray()
        );
    }

    private static bool IsMaintenanceFailure(Exception ex) =>
        ex
            is AssistantException
                or System.Text.Json.JsonException
                or HttpRequestException
                or OperationCanceledException;

    private async Task<object?> ExecuteExplicitAsync(
        string text,
        Guid sourceId,
        List<string> warnings,
        CancellationToken ct
    )
    {
        var remember = RememberCommand().Match(text);
        if (remember.Success)
        {
            var assessment = await AssessCommandAsync(text, warnings, ct);
            var entry = await memory.ProposeAsync(
                remember.Groups[1].Value,
                "Semantic",
                assessment.Sensitive,
                sourceId,
                assessment.Targets,
                false,
                ct
            );
            if (
                assessment.Validated
                && entry.Status == MemoryStatus.Pending
                && !entry.Sensitive
                && entry.Targets.Count == 0
            )
            {
                var approved = await memory.ApproveAsync(entry.Id, entry.Version, false, ct);
                warnings.AddRange(approved.Warnings);
                return new { action = "remember", memory = approved.Memory };
            }
            return new
            {
                action = "remember",
                memory = MemoryService.View(entry),
                needsApproval = entry.Status == MemoryStatus.Pending,
            };
        }
        var forget = ForgetCommand().Match(text);
        if (forget.Success && Guid.TryParse(forget.Groups[1].Value, out var forgetId))
        {
            await memory.ForgetAsync(forgetId, ct);
            return new { action = "forget", deletedId = forgetId };
        }
        var correct = CorrectCommand().Match(text);
        if (correct.Success && Guid.TryParse(correct.Groups[1].Value, out var correctId))
        {
            var target = await memory.GetAsync(correctId, ct);
            var assessment = await AssessCommandAsync(text, warnings, ct);
            var entry = await memory.ProposeAsync(
                correct.Groups[2].Value,
                target.Category,
                assessment.Sensitive,
                sourceId,
                [correctId],
                false,
                ct
            );
            if (assessment.Validated && entry.Status == MemoryStatus.Pending && !entry.Sensitive)
            {
                var approved = await memory.ApproveAsync(entry.Id, entry.Version, false, ct);
                warnings.AddRange(approved.Warnings);
                return new { action = "correct", memory = approved.Memory };
            }
            return new
            {
                action = "correct",
                memory = MemoryService.View(entry),
                needsApproval = true,
            };
        }
        return null;
    }

    private async Task<(bool Validated, bool Sensitive, Guid[] Targets)> AssessCommandAsync(
        string text,
        List<string> warnings,
        CancellationToken ct
    )
    {
        try
        {
            var active = (await memory.ListAsync("Active", ct)).Take(30).ToArray();
            var activeIds = active.Select(x => x.Id).ToHashSet();
            var drafts = (await processor.ExtractAsync(text, active, ct))
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.Evidence)
                    && text.Contains(x.Evidence, StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();
            var targets = drafts.SelectMany(x => x.Replaces ?? []).Distinct().ToArray();
            if (drafts.Length > 0 && targets.Length <= 5 && targets.All(activeIds.Contains))
                return (true, drafts.Any(x => x.Sensitive), targets);
        }
        catch (Exception ex) when (IsMaintenanceFailure(ex) && ex is not OperationCanceledException)
        { }
        warnings.Add(
            "No se pudo validar sensibilidad y contradicciones de la orden; el recuerdo necesita aprobación."
        );
        return (false, true, []); // Uncertain classification always requires explicit sensitive consent.
    }

    private async Task SummarizeAsync(Conversation conversation, CancellationToken ct)
    {
        var eligible = conversation
            .Messages.Where(x => !x.Excluded && x.Sequence > conversation.SummaryThroughSequence)
            .OrderBy(x => x.Sequence)
            .ToArray();
        if (eligible.Length < options.Value.SummaryTriggerMessages)
            return;
        var older = eligible
            .Take(Math.Max(0, eligible.Length - options.Value.RecentMessages))
            .ToArray();
        var batch = new List<ConversationMessage>();
        var size = 0;
        foreach (var message in older)
        {
            if (
                size + message.Content.Length
                > Math.Min(12000, options.Value.ContextCharacters - 4000)
            )
                break;
            batch.Add(message);
            size += message.Content.Length;
        }
        if (batch.Count == 0)
            return;
        conversation.Summary = await processor.SummarizeAsync(conversation.Summary, batch, ct);
        conversation.SummaryThroughSequence = batch[^1].Sequence;
        await unitOfWork.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"^(?:recuerda que\s+|/remember\s+)([\s\S]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RememberCommand();

    [GeneratedRegex(
        @"^(?:olvida el recuerdo\s+|/forget\s+)([a-f0-9-]{36})[.!]?$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ForgetCommand();

    [GeneratedRegex(
        @"^(?:corrige el recuerdo\s+|/correct\s+)([a-f0-9-]{36})\s*:\s*([\s\S]+)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex CorrectCommand();
}
