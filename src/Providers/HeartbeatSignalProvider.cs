using EndpointSignalAgent.Contracts;

namespace EndpointSignalAgent.Providers;

public sealed class HeartbeatSignalProvider : ISignalProvider
{
    public ValueTask<IReadOnlyList<SignalEvent>> CollectAsync(CancellationToken ct)
    {
        IReadOnlyList<SignalEvent> events = new[]
        {
            new SignalEvent(
                TimestampUtc: DateTimeOffset.UtcNow,
                Type: "Heartbeat",
                Payload: new Dictionary<string, string>
                {
                    ["user"] = Environment.UserName,
                    ["pid"]  = Environment.ProcessId.ToString()
                })
        };

        return ValueTask.FromResult(events);
    }
}
