using MemoryAssistant.Application.Services;
using MemoryAssistant.Domain.Entities;

namespace MemoryAssistant.Application.Tests;

public sealed class DependencyTests
{
    [Fact]
    public void Domain_references_only_the_runtime()
    {
        var references = typeof(MemoryEntry).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.All(references, name => Assert.True(name == "netstandard" || name.StartsWith("System") || name.StartsWith("Microsoft.CSharp"), name));
    }

    [Fact]
    public void Application_cannot_reference_presentation_storage_or_AI_frameworks()
    {
        var references = typeof(MemoryService).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.Contains("MemoryAssistant.Domain", references);
        Assert.DoesNotContain(references, name => name is "MemoryAssistant.Api" or "MemoryAssistant.Infrastructure" ||
            name.StartsWith("Microsoft.EntityFrameworkCore") || name.StartsWith("Microsoft.Extensions.AI") ||
            name.StartsWith("Microsoft.Agents") || name.StartsWith("Microsoft.AspNetCore") || name.StartsWith("Npgsql") || name.StartsWith("Pgvector") || name == "OpenAI");
    }
}
