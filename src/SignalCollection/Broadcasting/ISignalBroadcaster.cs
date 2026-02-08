using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.SignalCollection.Broadcasting;

/// <summary>
/// Broadcasts signals to multiple consumers (pub/sub pattern).
/// Ensures all registered consumers receive all signals.
/// </summary>
public interface ISignalBroadcaster
{
    /// <summary>
    /// Broadcasts a signal to all registered channel writers.
    /// Will attempt to write to all channels; logs failures but doesn't throw.
    /// </summary>
    Task BroadcastAsync(SignalEventType type, Dictionary<string, string> payload, string spoolPath, CancellationToken cancellationToken = default);
}
