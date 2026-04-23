using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.SignalCollection.Storage;

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
        var materializedEvents = signalEvents as SignalEvent[] ?? signalEvents.ToArray();
        var lines = new List<byte[]>(materializedEvents.Length);
        foreach (var ev in materializedEvents)
        {
            var lineObj = new
            {
                ts = ev.TimestampUtc,
                type = ev.Type.ToString(),
                payload = ev.Payload
            };

            var json = JsonSerializer.Serialize(lineObj, _jsonOptions);
            lines.Add(Encoding.UTF8.GetBytes(json + "\n"));
        }

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

                foreach (var lineBytes in lines)
                {
                    await fs.WriteAsync(lineBytes, ct);
                }

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
        // No resources to dispose
    }
}
