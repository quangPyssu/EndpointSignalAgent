using System.Text.Json;
using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Storage;

public sealed class ProgressStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(string manifestRoot, ProgressStateRecord progress, CancellationToken ct)
    {
        Directory.CreateDirectory(manifestRoot);
        var path = Path.Combine(manifestRoot, "progress_state.json");
        await AtomicJsonFileWriter.WriteAsync(path, progress, JsonOptions, ct);
    }

    public async Task<ProgressStateRecord?> LoadAsync(string manifestRoot, CancellationToken ct)
    {
        var path = Path.Combine(manifestRoot, "progress_state.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProgressStateRecord>(stream, JsonOptions, ct);
    }
}
