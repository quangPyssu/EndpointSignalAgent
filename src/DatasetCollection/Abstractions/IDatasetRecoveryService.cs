namespace EndpointSignalAgent.DatasetCollection.Abstractions;

public interface IDatasetRecoveryService
{
    Task<int> RecoverDanglingSessionsAsync(CancellationToken ct);
}
