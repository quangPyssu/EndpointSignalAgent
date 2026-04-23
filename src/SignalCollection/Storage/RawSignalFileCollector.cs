using System.Text;
using System.Text.Json;
using EndpointSignalAgent.SignalCollection.Contracts;

namespace EndpointSignalAgent.SignalCollection.Storage;

public sealed class RawSignalFileCollector : IDisposable
{
    private readonly string _spoolPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public RawSignalFileCollector(string spoolPath)
    {
        _spoolPath = spoolPath;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var dir = Path.GetDirectoryName(_spoolPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task WriteAsync(RawCollectorSignalRecord signalRecord, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(signalRecord, _jsonOptions);
        var lineBytes = Encoding.UTF8.GetBytes(json + "\n");

        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = new FileStream(
                    _spoolPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);

                await fs.WriteAsync(lineBytes, ct);
                await fs.FlushAsync(ct);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(delay, ct);
                delay += delay;
            }
        }
    }

    public void Dispose()
    {
        // no-op
    }
}
