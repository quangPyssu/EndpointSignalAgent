using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Abstractions;

public interface IProgressTrackingService
{
    Task<ProgressStateRecord> RecalculateAsync(CancellationToken ct);
    Task<ProgressStateRecord> GetCurrentAsync(CancellationToken ct);
}
