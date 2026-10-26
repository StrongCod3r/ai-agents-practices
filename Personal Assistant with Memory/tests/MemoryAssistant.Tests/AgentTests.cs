using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Infrastructure.Persistence;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MemoryAssistant.Tests;

public sealed class AgentTests
{
    [Fact]
    public async Task Framework_roundtrips_tool_results_and_returns_final_answer()
    {
        var chat = new FakeChat();
        chat.EnqueueTool("Lookup", new() { ["query"] = "C#" });
        chat.EnqueueText("Prefieres C#.");
        var invoked = false;
        var tool = AIFunctionFactory.Create((string query) => { invoked = true; return new { content = "C#", query }; }, "Lookup");
        var steps = new List<ToolStep>();
        var answer = await new AgentRunner(chat, Options.Create(new AgentOptions { Model = "fake" }))
            .RunAsync([new(ChatRole.User, "¿Qué prefiero?")], [tool], steps, default);
        Assert.True(invoked);
        Assert.Equal("Prefieres C#.", answer);
        Assert.Single(steps);
        Assert.Contains(chat.Requests[1].SelectMany(x => x.Contents), x => x is FunctionResultContent);
    }

    [Fact]
    public async Task Sequential_tools_can_use_previous_observations()
    {
        var chat = new FakeChat();
        chat.EnqueueTool("Step", new() { ["value"] = 1 });
        chat.EnqueueTool("Step", new() { ["value"] = 2 });
        chat.EnqueueText("Hecho");
        var values = new List<int>();
        var tool = AIFunctionFactory.Create((int value) => { values.Add(value); return value + 1; }, "Step");
        await new AgentRunner(chat, Options.Create(new AgentOptions())).RunAsync([new(ChatRole.User, "Continúa")], [tool], [], default);
        Assert.Equal(new[] { 1, 2 }, values);
        Assert.Equal(3, chat.Requests.Count);
    }

    [Theory]
    [InlineData(1, 10, "round_budget")]
    [InlineData(5, 1, "tool_budget")]
    public async Task Budgets_stop_tool_loops(int rounds, int tools, string code)
    {
        var chat = new FakeChat();
        for (var i = 0; i < 6; i++) chat.EnqueueTool("Loop", []);
        var runner = new AgentRunner(chat, Options.Create(new AgentOptions { MaxRounds = rounds, MaxToolCalls = tools }));
        var error = await Assert.ThrowsAsync<AssistantException>(() => runner.RunAsync([new(ChatRole.User, "Sigue")],
            [AIFunctionFactory.Create(() => "ok", "Loop")], [], default));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Unknown_tool_is_an_observation_without_execution()
    {
        var chat = new FakeChat();
        chat.EnqueueTool("ApproveMemory", []);
        chat.EnqueueText("Necesito aprobación.");
        var steps = new List<ToolStep>();
        await new AgentRunner(chat, Options.Create(new AgentOptions())).RunAsync([new(ChatRole.User, "Hola")], [], steps, default);
        Assert.Contains("invalid_input", System.Text.Json.JsonSerializer.Serialize(Assert.Single(steps).Result));
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_provider_call()
    {
        var chat = new FakeChat();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentRunner(chat, Options.Create(new AgentOptions()))
            .RunAsync([new(ChatRole.User, "Hola")], [], [], new CancellationToken(true)));
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Tool_context_is_bounded()
    {
        var chat = new FakeChat();
        chat.EnqueueTool("Huge", []);
        var error = await Assert.ThrowsAsync<AssistantException>(() => new AgentRunner(chat, Options.Create(new AgentOptions { ContextCharacters = 100 }))
            .RunAsync([new(ChatRole.User, "Hola")], [AIFunctionFactory.Create(() => new string('x', 200), "Huge")], [], default));
        Assert.Equal("context_budget", error.Code);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task History_is_restored_and_isolated_between_conversations()
    {
        using var h = new Harness();
        var first = await h.Conversations.CreateAsync(default);
        var second = await h.Conversations.CreateAsync(default);
        await h.Conversations.SendAsync(first.Id, "Mi proyecto se llama Aurora", default);
        await h.Conversations.SendAsync(first.Id, "¿Cómo se llama?", default);
        Assert.Contains(h.Chat.Requests[^1], m => m.Text.Contains("Aurora"));
        h.Db.ChangeTracker.Clear(); // Simulate reopening persisted state in another request.
        await h.Conversations.SendAsync(second.Id, "Hola", default);
        Assert.DoesNotContain(h.Chat.Requests[^1], m => m.Text.Contains("Aurora"));
    }

    [Fact]
    public async Task Extraction_failure_does_not_discard_answer_or_history()
    {
        using var h = new Harness();
        h.Processor.Fail = true;
        var conversation = await h.Conversations.CreateAsync(default);
        var response = await h.Conversations.SendAsync(conversation.Id, "Hola", default);
        Assert.Equal("Respuesta de prueba.", response.Answer);
        Assert.Contains(response.Warnings, x => x.Contains("extracción"));
        Assert.Equal(2, (await h.Conversations.GetAsync(conversation.Id, default)).Messages.Count);
    }

    [Fact]
    public async Task Natural_statement_produces_pending_candidate_and_no_activation()
    {
        using var h = new Harness();
        var conversation = await h.Conversations.CreateAsync(default);
        var result = await h.Conversations.SendAsync(conversation.Id, "Prefiero C#", default);
        Assert.Equal("Pending", Assert.Single(result.Candidates).Status);
        Assert.Empty(await h.Memory.ListAsync("Active", default));
    }

    [Fact]
    public async Task Extractor_output_without_literal_evidence_is_not_saved()
    {
        using var h = new Harness();
        h.Processor.Drafts = [new("Prefiero Rust", "Semantic", false, "Rust", [])];
        var conversation = await h.Conversations.CreateAsync(default);
        var result = await h.Conversations.SendAsync(conversation.Id, "Hola", default);
        Assert.Empty(result.Candidates);
        Assert.Empty(await h.Memory.ListAsync(null, default));
    }

    [Fact]
    public async Task Long_conversations_are_summarized_without_approving_candidates()
    {
        using var h = new Harness();
        h.AgentOptions.SummaryTriggerMessages = 4;
        h.AgentOptions.RecentMessages = 2;
        var conversation = await h.Conversations.CreateAsync(default);
        await h.Conversations.SendAsync(conversation.Id, "Prefiero C#", default);
        await h.Conversations.SendAsync(conversation.Id, "Continúa", default);
        Assert.Equal("Resumen de prueba", (await h.Conversations.GetAsync(conversation.Id, default)).Summary);
        Assert.Empty(await h.Memory.ListAsync("Active", default));
    }

    [Fact]
    public async Task Explicit_remember_activates_only_a_validated_nonsensitive_fact()
    {
        using var h = new Harness();
        h.Embeddings.Fail = true;
        var conversation = await h.Conversations.CreateAsync(default);
        await h.Conversations.SendAsync(conversation.Id, "Recuerda que Prefiero C#", default);
        Assert.Equal("Prefiero C#", Assert.Single(await h.Memory.ListAsync("Active", default)).Content);
    }

    [Fact]
    public async Task Explicit_sensitive_or_unclassified_fact_stays_pending()
    {
        using var h = new Harness();
        var conversation = await h.Conversations.CreateAsync(default);
        var response = await h.Conversations.SendAsync(conversation.Id, "Recuerda que tengo una condición personal", default);
        Assert.Equal("Pending", Assert.Single(response.Candidates).Status);
        Assert.True(response.Candidates[0].Sensitive);
        Assert.Empty(await h.Memory.ListAsync("Active", default));
    }

    [Fact]
    public async Task Explicit_remember_that_contradicts_an_active_fact_requires_approval()
    {
        using var h = new Harness();
        h.Embeddings.Fail = true;
        var first = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, null, null, false, default);
        await h.Memory.ApproveAsync(first.Id, 1, false, default);
        h.Processor.Drafts = [new("Prefiero Python", "Semantic", false, "Python", [first.Id])];
        var conversation = await h.Conversations.CreateAsync(default);
        var response = await h.Conversations.SendAsync(conversation.Id, "Recuerda que prefiero Python", default);
        Assert.Equal("Replace", Assert.Single(response.Candidates).Operation);
        Assert.Equal("Prefiero C#", Assert.Single(await h.Memory.ListAsync("Active", default)).Content);
    }

    [Fact]
    public async Task Explicit_forget_removes_the_fact_without_reextracting_the_command()
    {
        using var h = new Harness();
        var source = await h.SourceAsync("Prefiero C#");
        var first = await h.Memory.ProposeAsync("Prefiero C#", "Semantic", false, source.Id, null, false, default);
        await h.Memory.ApproveAsync(first.Id, 1, false, default);
        var response = await h.Conversations.SendAsync(source.ConversationId, $"Olvida el recuerdo {first.Id}", default);
        Assert.Empty(await h.Memory.ListAsync(null, default));
        Assert.Empty(response.Candidates);
        Assert.DoesNotContain(h.Chat.Requests[^1], x => x.Text.Contains("Prefiero C#"));
    }

    [Fact]
    public async Task Gate_blocks_other_mutations_until_current_turn_finishes_and_can_be_cancelled()
    {
        var gate = new LocalExecutionGate();
        var first = await gate.EnterAsync(default);
        var waiting = gate.EnterAsync(default);
        Assert.False(waiting.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(new CancellationToken(true)));
        first.Dispose();
        using var second = await waiting;
    }
}
