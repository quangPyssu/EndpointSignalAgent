using System.Text;
using System.Text.Json;
using EndpointSignalAgent.Contracts;

namespace EndpointSignalAgent.Providers;

public sealed class SpoolFileSignalProvider : ISignalProvider
{
    private readonly string _spoolPath;
    private readonly string _offsetPath;
    private long _offsetBytes;

    public SpoolFileSignalProvider(string spoolPath, string offsetPath)
    {
        _spoolPath = spoolPath;
        _offsetPath = offsetPath;
        _offsetBytes = TryLoadOffset(offsetPath);
    }

    public async ValueTask<IReadOnlyList<SignalEvent>> CollectAsync(CancellationToken ct)
    {
        if (!File.Exists(_spoolPath))
            return Array.Empty<SignalEvent>();

        // If file was rotated/truncated, reset offset
        var len = new FileInfo(_spoolPath).Length;
        if (len < _offsetBytes) _offsetBytes = 0;

        var events = new List<SignalEvent>(64);
        var utf8 = Encoding.UTF8;

        using var fs = new FileStream(_spoolPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_offsetBytes, SeekOrigin.Begin);

        using var sr = new StreamReader(fs, utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        while (!ct.IsCancellationRequested)
        {
            var line = await sr.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            // IMPORTANT: we assume writer uses '\n' newline; we update offset by bytes(line)+1
            _offsetBytes += utf8.GetByteCount(line) + 1;

            if (TryParseLineToSignalEvent(line, out var ev))
                events.Add(ev);
        }

        TrySaveOffset(_offsetPath, _offsetBytes);
        return events;
    }

    private static bool TryParseLineToSignalEvent(string line, out SignalEvent ev)
    {
        ev = default!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var ts = root.GetProperty("ts").GetDateTimeOffset();
            var typeRaw = root.GetProperty("type").GetString() ?? "Unknown";
            var type = Enum.TryParse<SignalEventType>(typeRaw, ignoreCase: true, out var parsedType)
                ? parsedType
                : SignalEventType.Unknown;

            var payloadDict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in payload.EnumerateObject())
                    payloadDict[p.Name] = p.Value.ToString();
            }

            ev = new SignalEvent(ts, type, payloadDict);
            return true;
        }
        catch
        {
            return false; // bad line; skip
        }
    }

    private static long TryLoadOffset(string path)
    {
        try { return File.Exists(path) ? long.Parse(File.ReadAllText(path)) : 0; }
        catch { return 0; }
    }

    private static void TrySaveOffset(string path, long offset)
    {
        try { File.WriteAllText(path, offset.ToString()); }
        catch { /* ignore */ }
    }
}
