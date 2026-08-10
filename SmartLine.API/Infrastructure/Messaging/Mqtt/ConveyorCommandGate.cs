namespace SmartLine.API.Infrastructure.Messaging.Mqtt;

/// <summary>
/// Serializes desired-state transitions and their retained MQTT publication for the supported
/// single-API-instance deployment. Emergency requests additionally raise a preemption signal so
/// an in-flight normal command cannot overwrite the immediate safety command.
/// </summary>
public sealed class ConveyorCommandGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private int pendingEmergencyCount;

    public bool IsEmergencyPending => Volatile.Read(ref pendingEmergencyCount) > 0;

    public IDisposable BeginEmergency()
    {
        Interlocked.Increment(ref pendingEmergencyCount);
        return new EmergencyLease(this);
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        return new SemaphoreLease(semaphore);
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref semaphore, null)?.Release();
    }

    private sealed class EmergencyLease(ConveyorCommandGate owner) : IDisposable
    {
        private ConveyorCommandGate? owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is not null)
                Interlocked.Decrement(ref current.pendingEmergencyCount);
        }
    }
}
