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

        try
        {
            if (File.Exists(path))
            {
                await ReplaceWithRetriesAsync(tempPath, path, ct);
            }
            else
            {
                try
                {
                    File.Move(tempPath, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    await ReplaceWithRetriesAsync(tempPath, path, ct);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task ReplaceWithRetriesAsync(string tempPath, string destinationPath, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(100);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                EnsureWritable(destinationPath);
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 3)
            {
                await Task.Delay(delay, ct);
                delay = delay + delay;
            }
        }

        EnsureWritable(destinationPath);
        File.Copy(tempPath, destinationPath, overwrite: true);
    }

    private static void EnsureWritable(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
    }
}
