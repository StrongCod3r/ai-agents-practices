using MemoryAssistant.Domain.Exceptions;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Infrastructure.Persistence;
using MemoryAssistant.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace MemoryAssistant.Tests;

public sealed class MemoryTests
{
    [Fact]
    public async Task Candidates_need_approval_and_rejected_facts_are_not_active()
    {
        using var h = new Harness();
        var source = await h.SourceAsync("Prefiero C#");
        var entry = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default);
        Assert.Empty(await h.Memory.ListAsync("Active", default));
        var rejected = await h.Memory.RejectAsync(entry.Id, 1, default);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Empty(await h.Memory.ListAsync("Active", default));
        await Assert.ThrowsAsync<AssistantException>(() => h.Memory.ApproveAsync(entry.Id, rejected.Version, false, default));
    }

    [Fact]
    public async Task Sensitive_facts_require_specific_confirmation_even_if_model_does_not_mark_them()
    {
        using var h = new Harness();
        var entry = await h.Memory.ProposeAsync("Tengo diabetes", "Semantic", false, null, null, false, default);
        Assert.True(entry.Sensitive);
        var error = await Assert.ThrowsAsync<AssistantException>(() => h.Memory.ApproveAsync(entry.Id, 1, false, default));
        Assert.Equal("sensitive_confirmation_required", error.Code);
        var approved = await h.Memory.ApproveAsync(entry.Id, 1, true, default);
        Assert.Equal("Active", approved.Memory!.Status);
    }

    [Theory]
    [InlineData("Mi contraseña es abc123")]
    [InlineData("api_key: abcdef123456")]
    [InlineData("sk-or-v1-012345678901234567890")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public async Task Credentials_are_rejected(string text)
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<DomainRuleException>(() => h.Memory.ProposeAsync(text, "Semantic", false, null, null, false, default));
        Assert.Empty(await h.Memory.ListAsync(null, default));
    }

    [Fact]
    public async Task Exact_duplicates_share_one_memory_and_preserve_both_sources()
    {
        using var h = new Harness();
        var a = await h.SourceAsync("Prefiero C#");
        var b = await h.SourceAsync("Prefiero C#");
        var first = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, a.Id, null, false, default);
        var second = await h.Memory.ProposeAsync("  prefiero   c#  ", "Semantic", false, b.Id, null, false, default);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Sources.Count);
        Assert.Single(await h.Memory.ListAsync("Pending", default));
    }

    [Fact]
    public async Task Credential_check_covers_the_entire_source_message()
    {
        using var h = new Harness();
        var source = await h.SourceAsync(new string('x', 2100) + " api_key: do-not-store Prefiero C#");
        await Assert.ThrowsAsync<DomainRuleException>(() => h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default));
        Assert.Empty(await h.Memory.ListAsync(null, default));
    }

    [Fact]
    public async Task Short_fact_can_be_extracted_from_a_long_valid_source()
    {
        using var h = new Harness();
        var source = await h.SourceAsync(new string('x', 3000) + " Prefiero C#");
        var entry = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default);
        Assert.Equal("Prefiero C#", entry.Content);
    }

    [Fact]
    public async Task Embedding_failure_keeps_approved_memory_pending_indexation()
    {
        using var h = new Harness();
        h.Embeddings.Fail = true;
        var candidate = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, null, null, false, default);
        var approved = await h.Memory.ApproveAsync(candidate.Id, 1, false, default);
        Assert.Equal("Active", approved.Memory!.Status);
        Assert.False(approved.Memory.Indexed);
        Assert.Single(approved.Warnings);
        var search = await h.Memory.SearchAsync("Lenguaje", default);
        Assert.False(search.Available);
        h.Embeddings.Fail = false;
        Assert.Equal(1, (await h.Memory.ReindexAsync(default)).Processed);
    }

    [Fact]
    public async Task Correction_is_a_proposal_until_approved_then_invalidates_old_context()
    {
        using var h = new Harness();
        var source = await h.SourceAsync("Prefiero C#");
        var conversation = await h.Db.Conversations.SingleAsync();
        conversation.Summary = "Prefiere C#";
        conversation.SummaryThroughSequence = 1;
        var original = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default);
        await h.Memory.ApproveAsync(original.Id, 1, false, default);
        var correction = await h.Memory.ProposeAsync("Prefiero Python", "Semantic", false, null, [original.Id], false, default);
        Assert.Equal(MemoryStatus.Active, original.Status);
        await h.Memory.ApproveAsync(correction.Id, 1, false, default);
        Assert.Equal(MemoryStatus.Superseded, original.Status);
        Assert.True(source.Excluded);
        Assert.Null(conversation.Summary);
        Assert.Null(original.Embedding);
        Assert.Equal("Prefiero Python", Assert.Single(await h.Memory.ListAsync("Active", default)).Content);
    }

    [Fact]
    public async Task Stale_candidate_cannot_overwrite_a_newer_correction()
    {
        using var h = new Harness();
        var original = await h.Memory.ProposeAsync("C#", "Semantic", false, null, null, false, default);
        await h.Memory.ApproveAsync(original.Id, 1, false, default);
        var a = await h.Memory.ProposeAsync("Python", "Semantic", false, null, [original.Id], false, default);
        var b = await h.Memory.ProposeAsync("Rust", "Semantic", false, null, [original.Id], false, default);
        await h.Memory.ApproveAsync(a.Id, 1, false, default);
        var error = await Assert.ThrowsAsync<AssistantException>(() => h.Memory.ApproveAsync(b.Id, 1, false, default));
        Assert.Equal(FailureKind.Conflict, error.Kind);
        Assert.Equal("Python", Assert.Single(await h.Memory.ListAsync("Active", default)).Content);
    }

    [Fact]
    public async Task Forget_deletes_lineage_vectors_candidates_and_excludes_origin_and_usage_turns()
    {
        using var h = new Harness();
        var source = await h.SourceAsync("Prefiero C#");
        var usage = await h.SourceAsync("¿Qué lenguaje prefiero?");
        var first = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default);
        await h.Memory.ApproveAsync(first.Id, 1, false, default);
        var newer = await h.Memory.ProposeAsync("Prefiero Python", "Semantic", false, null, [first.Id], false, default);
        await h.Memory.ApproveAsync(newer.Id, 1, false, default);
        await h.Memory.RecordUsageAsync([newer.Id], usage.Id, default);
        await h.Memory.ProposeAsync("Prefiero Rust", "Semantic", false, null, [newer.Id], false, default);
        await h.Memory.ForgetAsync(newer.Id, default);
        Assert.Empty(await h.Memory.ListAsync(null, default));
        Assert.Empty(await h.Db.Sources.ToListAsync());
        Assert.Empty(await h.Db.Targets.ToListAsync());
        Assert.True(source.Excluded);
        Assert.True(usage.Excluded);
        await Assert.ThrowsAsync<AssistantException>(() => h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default));
    }

    [Fact]
    public async Task Consolidation_never_approves_its_own_proposals()
    {
        using var h = new Harness();
        var first = await h.Memory.ProposeAsync("Uso C#", "Semantic", false, null, null, false, default);
        var second = await h.Memory.ProposeAsync("Desarrollo en .NET", "Semantic", false, null, null, false, default);
        await h.Memory.ApproveAsync(first.Id, 1, false, default);
        await h.Memory.ApproveAsync(second.Id, 1, false, default);
        h.Processor.Drafts = [new("Uso C# y .NET", "Semantic", false, "", [first.Id, second.Id])];
        var proposals = await new ConsolidationService(h.Memory, h.Processor).ProposeAsync(default);
        Assert.Equal("Pending", Assert.Single(proposals).Status);
        Assert.Equal(2, (await h.Memory.ListAsync("Active", default)).Count);
    }
}
