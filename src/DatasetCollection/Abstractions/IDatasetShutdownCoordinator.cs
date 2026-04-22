namespace EndpointSignalAgent.DatasetCollection.Abstractions;

public interface IDatasetShutdownCoordinator
{
    Task FinalizeAsync(string reason, CancellationToken ct);
}
