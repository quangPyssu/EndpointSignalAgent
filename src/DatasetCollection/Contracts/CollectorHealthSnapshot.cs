namespace EndpointSignalAgent.DatasetCollection.Contracts;

public sealed record CollectorHealthSnapshot(
    bool SignalWriterRunning,
    bool SessionStateCollectorRunning,
    bool ApplicationUsageCollectorRunning,
    bool NetworkContextCollectorRunning,
    bool SystemResourceCollectorRunning,
    bool CollectionPaused,
    DateTimeOffset CapturedAtUtc);
