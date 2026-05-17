using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class DatasetExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly DatasetCollectionOptions _options;
    private readonly ICollectionManifestService _manifestService;
    private readonly IProgressTrackingService _progressTracking;
    private readonly ICollectionControl _collectionControl;

    public DatasetExportService(
        IOptions<DatasetCollectionOptions> options,
        ICollectionManifestService manifestService,
        IProgressTrackingService progressTracking,
        ICollectionControl collectionControl)
    {
        _options = options.Value;
        _manifestService = manifestService;
        _progressTracking = progressTracking;
        _collectionControl = collectionControl;
    }

    public async Task<string> ExportParticipantPackageAsync(string participantId, string agentVersion, CancellationToken ct)
    {
        var exportDir = Path.Combine(_options.ExportRoot, $"participant_{participantId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(exportDir);

        var files = new List<string>();
        var rawSource = Path.Combine("spool", "raw_signals.jsonl");
        if (File.Exists(rawSource))
        {
            var rawTarget = Path.Combine(exportDir, "raw_signals.jsonl");
            File.Copy(rawSource, rawTarget, overwrite: true);
            files.Add(rawTarget);
        }

        var manifestFiles = Directory.Exists(_options.ManifestRoot)
            ? Directory.GetFiles(_options.ManifestRoot, "*.json", SearchOption.TopDirectoryOnly)
            : [];

        foreach (var file in manifestFiles)
        {
            var target = Path.Combine(exportDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            files.Add(target);
        }

        var progress = await _progressTracking.GetCurrentAsync(ct);
        var progressPath = Path.Combine(exportDir, "progress_snapshot.json");
        await File.WriteAllTextAsync(progressPath, JsonSerializer.Serialize(progress, JsonOptions), ct);
        files.Add(progressPath);

        var health = new CollectorHealthSnapshot(true, true, true, true, true, _collectionControl.IsPaused, DateTimeOffset.UtcNow);
        var healthPath = Path.Combine(exportDir, "collector_health_snapshot.json");
        await File.WriteAllTextAsync(healthPath, JsonSerializer.Serialize(health, JsonOptions), ct);
        files.Add(healthPath);

        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            checksums[name] = ComputeSha256(file);
        }
        var exportManifest = new DatasetExportManifest(
            ExportTimeUtc: DateTimeOffset.UtcNow,
            AgentVersion: agentVersion,
            SchemaVersion: "dataset-collection-v1",
            ExportType: "participant",
            Selection: participantId,
            Checksums: checksums);

        var manifestPath = Path.Combine(exportDir, "dataset_export_manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(exportManifest, JsonOptions), ct);
        return exportDir;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}
