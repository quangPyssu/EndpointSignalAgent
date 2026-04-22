using System.Text;
using System.Text.Json;

namespace EndpointSignalAgent.DatasetCollection.Storage;

internal static class AtomicJsonFileWriter
{
    public static async Task WriteAsync<T>(string path, T payload, JsonSerializerOptions options, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? string.Empty, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(payload, options);

        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
