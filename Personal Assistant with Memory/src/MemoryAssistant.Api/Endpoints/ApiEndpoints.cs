using MemoryAssistant.Api.Contracts;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Services;
using Scalar.AspNetCore;

namespace MemoryAssistant.Api.Endpoints;

public static class ApiEndpoints
{
    public static void MapApiEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet(
            "/health/ready",
            async (IDataStoreHealth health, CancellationToken ct) =>
                await health.IsReadyAsync(ct)
                    ? Results.Ok(new { status = "ready" })
                    : Results.StatusCode(503)
        );
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(
            async (invocation, next) =>
            {
                var gate =
                    invocation.HttpContext.RequestServices.GetRequiredService<LocalExecutionGate>();
                using var lease = await gate.EnterAsync(invocation.HttpContext.RequestAborted);
                return await next(invocation);
            }
        );
        api.MapGet(
            "/conversations",
            (ConversationService service, CancellationToken ct) => service.ListAsync(ct)
        );
        api.MapPost(
            "/conversations",
            async (ConversationService service, CancellationToken ct) =>
            {
                var conversation = await service.CreateAsync(ct);
                return Results.Created($"/api/conversations/{conversation.Id}", conversation);
            }
        );
        api.MapGet(
            "/conversations/{id:guid}",
            (Guid id, ConversationService service, CancellationToken ct) => service.GetAsync(id, ct)
        );
        api.MapDelete(
            "/conversations/{id:guid}",
            async (Guid id, ConversationService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return Results.NoContent();
            }
        );
        api.MapPost(
            "/conversations/{id:guid}/messages",
            (Guid id, MessageRequest request, ConversationService service, CancellationToken ct) =>
                service.SendAsync(id, request.Message, ct)
        );
        api.MapGet(
            "/memories",
            (string? status, MemoryService service, CancellationToken ct) =>
                service.ListAsync(status, ct)
        );
        api.MapGet(
            "/memories/{id:guid}",
            (Guid id, MemoryService service, CancellationToken ct) => service.GetViewAsync(id, ct)
        );
        api.MapPost(
            "/memories",
            (CreateMemoryRequest request, MemoryService service, CancellationToken ct) =>
                service.SaveExplicitAsync(
                    request.Content,
                    request.Category,
                    request.Sensitive,
                    request.ConfirmSensitive,
                    ct
                )
        );
        api.MapPatch(
            "/memories/{id:guid}",
            (Guid id, UpdateMemoryRequest request, MemoryService service, CancellationToken ct) =>
                service.CorrectExplicitAsync(
                    id,
                    request.Content,
                    request.Version,
                    request.Category,
                    request.Sensitive,
                    request.ConfirmSensitive,
                    ct
                )
        );
        api.MapDelete(
            "/memories/{id:guid}",
            async (Guid id, MemoryService service, CancellationToken ct) =>
            {
                await service.ForgetAsync(id, ct);
                return Results.NoContent();
            }
        );
        api.MapPost(
            "/memories/search",
            (SearchRequest request, MemoryService service, CancellationToken ct) =>
                service.SearchAsync(request.Query, ct)
        );
        api.MapPost(
            "/memory-candidates/{id:guid}/approve",
            async (Guid id, ApproveRequest request, MemoryService service, CancellationToken ct) =>
            {
                var result = await service.ApproveAsync(
                    id,
                    request.Version,
                    request.ConfirmSensitive,
                    ct
                );
                return Results.Ok(new { memory = result.Memory, warnings = result.Warnings });
            }
        );
        api.MapPost(
            "/memory-candidates/{id:guid}/reject",
            (Guid id, RejectRequest request, MemoryService service, CancellationToken ct) =>
                service.RejectAsync(id, request.Version, ct)
        );
        api.MapPost(
            "/memories/consolidate",
            (ConsolidationService service, CancellationToken ct) => service.ProposeAsync(ct)
        );
        api.MapPost(
            "/memories/reindex",
            (MemoryService service, CancellationToken ct) => service.ReindexAsync(ct)
        );
    }
}
