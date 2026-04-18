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
        using var fs = new FileStream(
            _spoolPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var json = JsonSerializer.Serialize(signalRecord, _jsonOptions);
        var lineBytes = Encoding.UTF8.GetBytes(json + "\n");

        await fs.WriteAsync(lineBytes, ct);
        await fs.FlushAsync(ct);
    }

    public void Dispose()
    {
        // no-op
    }
}
