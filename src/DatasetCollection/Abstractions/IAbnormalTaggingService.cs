using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Abstractions;

public interface IAbnormalTaggingService
{
    Task<AbnormalAnnotationRecord> StartAbnormalSegmentAsync(string scenarioCode, string scenarioLabel, string initiatedBy, double confidence, string? notes, CancellationToken ct);
    Task<AbnormalAnnotationRecord?> EndAbnormalSegmentAsync(string initiatedBy, string? notes, CancellationToken ct);
    Task<AbnormalAnnotationRecord?> MarkLastMinutesAbnormalAsync(int minutes, string scenarioCode, string scenarioLabel, string initiatedBy, double confidence, string? notes, CancellationToken ct);
    Task<IReadOnlyList<AbnormalAnnotationRecord>> GetAnnotationsAsync(CancellationToken ct);
    Task<AbnormalAnnotationRecord?> GetActiveAnnotationAsync(CancellationToken ct);
}
