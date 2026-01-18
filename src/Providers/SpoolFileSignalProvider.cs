using System.Buffers;
using System.Text;
using System.Text.Json;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Providers;

public sealed class SpoolFileSignalProvider : ISignalProvider
{
    private readonly string _spoolPath;
    private readonly string _offsetPath;
    private readonly ILogger<SpoolFileSignalProvider>? _logger;
    private long _offsetBytes;

    public SpoolFileSignalProvider(string spoolPath, string offsetPath, ILogger<SpoolFileSignalProvider>? logger = null)
    {
        _spoolPath = spoolPath;
        _offsetPath = offsetPath;
        _logger = logger;
        _offsetBytes = TryLoadOffset(offsetPath);
        
        _logger?.LogInformation("SpoolFileSignalProvider initialized: path={SpoolPath}, initialOffset={Offset}", 
            _spoolPath, _offsetBytes);
    }

    public async ValueTask<IReadOnlyList<SignalEvent>> CollectAsync(CancellationToken ct)
    {
        if (!File.Exists(_spoolPath))
        {
            _logger?.LogDebug("Spool file does not exist: {SpoolPath}", _spoolPath);
            return Array.Empty<SignalEvent>();
        }

        var fileInfo = new FileInfo(_spoolPath);
        var fileSize = fileInfo.Length;
        
        _logger?.LogDebug("Reading spool: size={FileSize}, currentOffset={Offset}", fileSize, _offsetBytes);
        
        if (fileSize < _offsetBytes)
        {
            _logger?.LogWarning("Spool file truncated/rotated: fileSize={FileSize} < offset={Offset}, resetting offset", 
                fileSize, _offsetBytes);
            _offsetBytes = 0;
        }

        if (fileSize == _offsetBytes)
        {
            _logger?.LogDebug("No new data in spool (offset matches file size)");
            return Array.Empty<SignalEvent>();
        }

        var events = new List<SignalEvent>(64);

        using var fs = new FileStream(_spoolPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        fs.Seek(_offsetBytes, SeekOrigin.Begin);

        // Read bytes from offset to EOF
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int bytesRead;
            var buffer = new List<byte>(64 * 1024);
            var totalBytesRead = 0;

            while ((bytesRead = await fs.ReadAsync(rented.AsMemory(0, rented.Length), ct)) > 0)
            {
                buffer.AddRange(rented.AsSpan(0, bytesRead).ToArray());
                totalBytesRead += bytesRead;
            }

            _logger?.LogDebug("Read {BytesRead} bytes from spool", totalBytesRead);

            // Process complete lines separated by '\n'
            int lineStart = 0;
            int lineCount = 0;
            int parseFailures = 0;
            
            for (int i = 0; i < buffer.Count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;

                // [lineStart, i) is the line bytes (may end with '\r')
                int lineLen = i - lineStart;
                if (lineLen > 0 && buffer[lineStart + lineLen - 1] == (byte)'\r')
                    lineLen--;

                var lineBytes = buffer.GetRange(lineStart, lineLen).ToArray();
                var line = Encoding.UTF8.GetString(lineBytes);

                // Only advance offset for bytes up to and including '\n'
                _offsetBytes += (i - lineStart) + 1;

                lineCount++;
                
                if (line.Length > 0)
                {
                    if (TryParseLineToSignalEvent(line, out var ev))
                    {
                        events.Add(ev);
                        _logger?.LogTrace("Parsed event: type={Type}, ts={Timestamp}", ev.Type, ev.TimestampUtc);
                    }
                    else
                    {
                        parseFailures++;
                        _logger?.LogWarning("Failed to parse line {LineNumber}: {Line}", lineCount, 
                            line.Length > 100 ? line[..100] + "..." : line);
                    }
                }

                lineStart = i + 1;
            }

            // IMPORTANT:
            // If the file ended without '\n', we do NOT advance offset for that partial line.
            // It will be re-read next tick once the writer finishes the line.
            var partialLineBytes = buffer.Count - lineStart;
            if (partialLineBytes > 0)
            {
                _logger?.LogDebug("Partial line detected ({Bytes} bytes), will retry next collection", partialLineBytes);
            }

            TrySaveOffsetAtomic(_offsetPath, _offsetBytes);
            
            _logger?.LogInformation("Spool collection complete: read {LineCount} lines, parsed {EventCount} events, {FailureCount} parse failures, newOffset={Offset}",
                lineCount, events.Count, parseFailures, _offsetBytes);
            
            return events;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
            return false;
        }
    }

    private static long TryLoadOffset(string path)
    {
        try { return File.Exists(path) ? long.Parse(File.ReadAllText(path)) : 0; }
        catch { return 0; }
    }

    private static void TrySaveOffsetAtomic(string path, long offset)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, offset.ToString());
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
        }
        catch { /* ignore */ }
    }
}
