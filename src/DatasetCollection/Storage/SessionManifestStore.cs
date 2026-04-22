using System.Text.Json;
using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Storage;

public sealed class SessionManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveSessionAsync(string manifestRoot, CollectionSessionRecord session, CancellationToken ct)
    {
        Directory.CreateDirectory(manifestRoot);
        var path = Path.Combine(manifestRoot, $"session_{session.SessionId}.json");
        await AtomicJsonFileWriter.WriteAsync(path, session, JsonOptions, ct);
    }

    public async Task<IReadOnlyList<CollectionSessionRecord>> LoadAllSessionsAsync(string manifestRoot, CancellationToken ct)
    {
        if (!Directory.Exists(manifestRoot))
        {
            return [];
        }

        var files = Directory.GetFiles(manifestRoot, "session_*.json")
            .Where(f => !f.EndsWith(".annotations.json", StringComparison.OrdinalIgnoreCase));

        var sessions = new List<CollectionSessionRecord>();
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var session = await JsonSerializer.DeserializeAsync<CollectionSessionRecord>(stream, JsonOptions, ct);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions.OrderBy(s => s.StartedAtUtc ?? DateTimeOffset.MinValue).ToList();
    }
}
