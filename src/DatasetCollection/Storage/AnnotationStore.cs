using System.Text.Json;
using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Storage;

public sealed class AnnotationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(string manifestRoot, string sessionId, IReadOnlyList<AbnormalAnnotationRecord> annotations, CancellationToken ct)
    {
        Directory.CreateDirectory(manifestRoot);
        var path = Path.Combine(manifestRoot, $"session_{sessionId}.annotations.json");
        var tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(annotations, JsonOptions), ct);
        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<IReadOnlyList<AbnormalAnnotationRecord>> LoadAsync(string manifestRoot, string sessionId, CancellationToken ct)
    {
        var path = Path.Combine(manifestRoot, $"session_{sessionId}.annotations.json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var annotations = await JsonSerializer.DeserializeAsync<List<AbnormalAnnotationRecord>>(stream, JsonOptions, ct);
        return annotations ?? [];
    }
}
