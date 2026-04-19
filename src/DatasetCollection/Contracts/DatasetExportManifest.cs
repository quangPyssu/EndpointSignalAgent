namespace EndpointSignalAgent.DatasetCollection.Contracts;

public sealed record DatasetExportManifest(
    DateTimeOffset ExportTimeUtc,
    string AgentVersion,
    string SchemaVersion,
    string ExportType,
    string Selection,
    IReadOnlyDictionary<string, string> Checksums);
