using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointSignalAgent.Contracts;

namespace EndpointSignalAgent.Collectors;

public sealed class SpoolFileCollector : IDisposable
{
    private readonly string _spoolPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public SpoolFileCollector(string spoolPath)
    {
        _spoolPath = spoolPath;
        _jsonOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var dir = Path.GetDirectoryName(_spoolPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public async Task WriteAsync(SignalEvent signalEvent, CancellationToken ct = default)
    {
        await WriteAsync(new[] { signalEvent }, ct);
    }

    public async Task WriteAsync(IEnumerable<SignalEvent> signalEvents, CancellationToken ct = default)
    {
        using var fs = new FileStream(
            _spoolPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        foreach (var ev in signalEvents)
        {
            var lineObj = new
            {
                ts = ev.TimestampUtc,
                type = ev.Type.ToString(),
                payload = ev.Payload
            };

            var json = JsonSerializer.Serialize(lineObj, _jsonOptions);
            var lineBytes = Encoding.UTF8.GetBytes(json + "\n");

            await fs.WriteAsync(lineBytes, ct);
        }

        await fs.FlushAsync(ct);
    }

    public void Dispose()
    {
        // No resources to dispose
    }
}
