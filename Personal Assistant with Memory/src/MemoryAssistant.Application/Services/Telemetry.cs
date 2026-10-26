using System.Diagnostics;

namespace MemoryAssistant.Application.Services;

public static class Telemetry
{
    public const string Name = "MemoryAssistant";
    public static readonly ActivitySource Source = new(Name);
}

// A single local API instance: serialize the full turn and all memory mutations, including forget.
// The lock also prevents a response already in flight from reintroducing a forgotten memory.
public sealed class LocalExecutionGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
