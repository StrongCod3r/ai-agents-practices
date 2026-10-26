using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MemoryAssistant.Application.Contracts;

namespace MemoryAssistant.Tests;

public sealed class HttpTests
{
    [Fact]
    public async Task Wrapped_database_failures_return_a_readable_safe_error_to_the_chat()
    {
        using var factory = new ApiFactory();
        factory.Chat.Responses.Enqueue((_, _) => throw new InvalidOperationException("private wrapper", new Npgsql.NpgsqlException("private database details")));
        using var client = factory.CreateClient();
        var conversation = (await (await client.PostAsync("/api/conversations", null)).Content.ReadFromJsonAsync<ConversationView>())!;
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new { message = "Hola" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database_unavailable", body);
        Assert.DoesNotContain("private", body);
    }
    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/styles.css", "text/css")]
    [InlineData("/app.js", "javascript")]
    [InlineData("/chat-core.mjs", "javascript")]
    public async Task Chat_frontend_and_modules_are_served_by_the_same_application(string path, string mediaType)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Contains(mediaType, response.Content.Headers.ContentType!.MediaType!);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        if (path == "/") Assert.Contains("id=\"chat-form\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Conversation_listing_recovers_history_and_forgetting_removes_the_title()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var conversation = (await (await client.PostAsync("/api/conversations", null)).Content.ReadFromJsonAsync<ConversationView>())!;
        var sent = await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new { message = "Prefiero C#" });
        sent.EnsureSuccessStatusCode();
        var turn = (await sent.Content.ReadFromJsonAsync<MessageResponse>())!;
        var listed = Assert.Single((await client.GetFromJsonAsync<ConversationSummary[]>("/api/conversations"))!);
        Assert.Equal(conversation.Id, listed.Id);
        Assert.Equal("Prefiero C#", listed.Title);
        Assert.Equal(2, listed.MessageCount);
        (await client.DeleteAsync($"/api/memories/{turn.Candidates[0].Id}")).EnsureSuccessStatusCode();
        var forgotten = Assert.Single((await client.GetFromJsonAsync<ConversationSummary[]>("/api/conversations"))!);
        Assert.Equal("Nueva conversación", forgotten.Title);
        Assert.Equal(0, forgotten.MessageCount);
    }

    [Fact]
    public async Task Health_openapi_and_scalar_work_without_external_services()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar/v1")).StatusCode);
        var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/conversations/{id}/messages", out _));
        Assert.True(paths.TryGetProperty("/api/memories/search", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_message_returns_structured_error(string message)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/conversations/{Guid.NewGuid()}/messages", new { message });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_input", error.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Invalid_json_and_missing_resource_are_handled()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync($"/api/conversations/{Guid.NewGuid()}/messages", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/conversations/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Http_candidate_approval_version_check_and_forget_complete_the_lifecycle()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var created = await client.PostAsync("/api/conversations", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<ConversationView>())!;
        var sent = await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new { message = "Prefiero C#" });
        sent.EnsureSuccessStatusCode();
        var result = (await sent.Content.ReadFromJsonAsync<MessageResponse>())!;
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Pending", candidate.Status);
        var approved = await client.PostAsJsonAsync($"/api/memory-candidates/{candidate.Id}/approve", new { version = candidate.Version });
        approved.EnsureSuccessStatusCode();
        var approval = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        Assert.Equal("Active", approval.RootElement.GetProperty("memory").GetProperty("status").GetString());
        var stale = await client.PatchAsJsonAsync($"/api/memories/{candidate.Id}", new { content = "Prefiero Python", version = candidate.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/memories/{candidate.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<MemoryView[]>("/api/memories"))!);
        var forgotten = (await client.GetFromJsonAsync<ConversationView>($"/api/conversations/{conversation.Id}"))!;
        Assert.True(forgotten.Messages.All(x => x.Excluded));
        Assert.DoesNotContain(forgotten.Messages, x => x.Content.Contains("C#"));
    }

    [Fact]
    public async Task Explicit_http_save_returns_a_serializable_approval_and_patch_creates_new_version()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var saved = await client.PostAsJsonAsync("/api/memories", new { content = "Prefiero C#" });
        saved.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("memory");
        var id = body.GetProperty("id").GetGuid();
        var version = body.GetProperty("version").GetInt32();
        Assert.Equal("Active", body.GetProperty("status").GetString());
        var updated = await client.PatchAsJsonAsync($"/api/memories/{id}", new { content = "Prefiero Python", version });
        updated.EnsureSuccessStatusCode();
        var changed = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("memory");
        Assert.NotEqual(id, changed.GetProperty("id").GetGuid());
        Assert.Equal("Prefiero Python", changed.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Sensitive_http_save_stays_pending_until_specific_consent()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var saved = await client.PostAsJsonAsync("/api/memories", new { content = "Tengo diabetes" });
        saved.EnsureSuccessStatusCode();
        var entry = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("memory");
        Assert.Equal("Pending", entry.GetProperty("status").GetString());
        var id = entry.GetProperty("id").GetGuid();
        var denied = await client.PostAsJsonAsync($"/api/memory-candidates/{id}/approve", new { version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var approved = await client.PostAsJsonAsync($"/api/memory-candidates/{id}/approve", new { version = 1, confirmSensitive = true });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task Full_conversation_deletion_removes_derived_memories_and_history()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var conversation = (await (await client.PostAsync("/api/conversations", null)).Content.ReadFromJsonAsync<ConversationView>())!;
        (await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new { message = "Prefiero C#" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/conversations/{conversation.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/conversations/{conversation.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<MemoryView[]>("/api/memories"))!);
    }
}
