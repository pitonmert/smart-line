using System.Collections.Concurrent;

namespace SmartLine.API.Tests.Infrastructure;

using SmartLine.API.Shared;

public sealed record MqttCall(string Operation, string? Topic = null, string? Payload = null);

public sealed class RecordingMqttService : IMqttService
{
    private readonly ConcurrentQueue<MqttCall> _calls = new();
    private readonly object publishBlockLock = new();
    private Exception? _nextFailure;
    private ConveyorPublishBlock? nextConveyorPublishBlock;

    public event Func<string, string, Task>? OnMessageReceived;

    public IReadOnlyList<MqttCall> Calls => _calls.ToArray();
    public bool IsConnected => true;

    public Task StartAsync(CancellationToken ct = default)
    {
        _calls.Enqueue(new MqttCall(nameof(StartAsync)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _calls.Enqueue(new MqttCall(nameof(StopAsync)));
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topicFilter, CancellationToken ct = default)
    {
        _calls.Enqueue(new MqttCall(nameof(SubscribeAsync), topicFilter));
        return Task.CompletedTask;
    }

    public Task PublishAsync(
        string topic,
        string payload,
        bool retain = false,
        CancellationToken ct = default
    )
    {
        _calls.Enqueue(new MqttCall(nameof(PublishAsync), topic, payload));
        return CompleteOrFail();
    }

    public Task PublishSpeedChangeAsync(int speed, CancellationToken ct = default)
    {
        _calls.Enqueue(new MqttCall(nameof(PublishSpeedChangeAsync), Payload: speed.ToString()));
        return CompleteOrFail();
    }

    public async Task PublishConveyorStateAsync(
        bool isRunning,
        bool emergency,
        int resetGeneration,
        string acknowledgedFireEpoch,
        long acknowledgedFireGeneration,
        CancellationToken ct = default
    )
    {
        var call = new MqttCall(
            nameof(PublishConveyorStateAsync),
            Payload: $"running={isRunning};emergency={emergency};resetGeneration={resetGeneration};acknowledgedFireEpoch={acknowledgedFireEpoch};acknowledgedFireGeneration={acknowledgedFireGeneration}"
        );
        _calls.Enqueue(call);
        await CompleteOrFail();

        ConveyorPublishBlock? publishBlock = null;
        lock (publishBlockLock)
        {
            if (nextConveyorPublishBlock?.Matches(call) == true)
            {
                publishBlock = nextConveyorPublishBlock;
                nextConveyorPublishBlock = null;
            }
        }
        if (publishBlock is not null)
            await publishBlock.WaitAsync(ct);
    }

    public Task PublishFanAsync(bool active, CancellationToken ct = default)
    {
        _calls.Enqueue(new MqttCall(nameof(PublishFanAsync), Payload: active.ToString()));
        return CompleteOrFail();
    }

    public async Task RaiseMessageAsync(string topic, string payload)
    {
        if (OnMessageReceived is not null)
            await OnMessageReceived.Invoke(topic, payload);
    }

    public void Reset()
    {
        while (_calls.TryDequeue(out _)) { }
        _nextFailure = null;
        lock (publishBlockLock)
        {
            nextConveyorPublishBlock?.Release();
            nextConveyorPublishBlock = null;
        }
    }

    public ConveyorPublishBlock BlockNextConveyorPublish(Func<MqttCall, bool> predicate)
    {
        var publishBlock = new ConveyorPublishBlock(predicate);
        lock (publishBlockLock)
        {
            if (nextConveyorPublishBlock is not null)
                throw new InvalidOperationException("A conveyor publish block is already active.");
            nextConveyorPublishBlock = publishBlock;
        }
        return publishBlock;
    }

    public void FailNext(Exception? exception = null) =>
        _nextFailure =
            exception
            ?? new MqttUnavailableException(
                "MQTT unavailable in test.",
                new InvalidOperationException("Synthetic broker failure.")
            );

    private Task CompleteOrFail()
    {
        var failure = Interlocked.Exchange(ref _nextFailure, null);
        return failure is null ? Task.CompletedTask : Task.FromException(failure);
    }

    public sealed class ConveyorPublishBlock(Func<MqttCall, bool> predicate) : IDisposable
    {
        private readonly TaskCompletionSource entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Entered => entered.Task;

        internal bool Matches(MqttCall call) => predicate(call);

        internal async Task WaitAsync(CancellationToken ct)
        {
            entered.TrySetResult();
            await released.Task.WaitAsync(ct);
        }

        public void Release() => released.TrySetResult();

        public void Dispose() => Release();
    }
}
