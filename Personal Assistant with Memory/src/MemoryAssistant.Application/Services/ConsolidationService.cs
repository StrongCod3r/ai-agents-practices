using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Domain.Entities;

namespace MemoryAssistant.Application.Services;

public sealed class ConsolidationService(MemoryService memory, IMemoryProcessor processor)
{
    public async Task<IReadOnlyList<MemoryView>> ProposeAsync(CancellationToken ct)
    {
        var active = (await memory.ListAsync("Active", ct)).Take(30).ToArray();
        if (active.Length < 2)
            return [];
        var drafts = await processor.ConsolidateAsync(active, ct);
        var validIds = active.Select(x => x.Id).ToHashSet();
        var result = new List<MemoryView>();
        foreach (var draft in drafts)
        {
            var targets = draft.Replaces?.Distinct().ToArray() ?? [];
            if (targets.Length is < 2 or > 5 || targets.Any(x => !validIds.Contains(x)))
                continue;
            var entry = await memory.ProposeAsync(
                draft.Content,
                draft.Category,
                draft.Sensitive,
                null,
                targets,
                false,
                ct
            );
            if (entry.Status == MemoryStatus.Pending)
                result.Add(MemoryService.View(entry));
        }
        return result;
    }
}
