using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.SignalCollection.Broadcasting;

/// <summary>
/// Canonical signal envelope used across broadcast channels.
/// TimestampUtc is assigned at broadcast time and must be treated as event time.
/// </summary>
public readonly record struct BroadcastSignal(
    DateTimeOffset TimestampUtc,
    SignalEventType Type,
    Dictionary<string, string> Payload,
    string SpoolPath);
