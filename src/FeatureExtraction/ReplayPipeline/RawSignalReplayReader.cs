using System.Runtime.CompilerServices;
using System.Text.Json;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.SignalCollection.Contracts;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.ReplayPipeline;

internal sealed class RawSignalReplayReader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async IAsyncEnumerable<RawCollectorSignalRecord> ReadRecordsAsync(
        string rawSignalsPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var line in File.ReadLinesAsync(rawSignalsPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RawCollectorSignalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<RawCollectorSignalRecord>(line, JsonOpts);
            }
            catch (JsonException)
            {
                continue;
            }
            if (record is not null) yield return record;
        }
    }

    /// <summary>
    /// Returns only records belonging to the given session, as FeatureSignals sorted by timestamp.
    /// </summary>
    public async Task<List<FeatureSignal>> LoadSessionSignalsAsync(
        string rawSignalsPath,
        string sessionId,
        CancellationToken ct = default)
    {
        var signals = new List<FeatureSignal>();
        await foreach (var record in ReadRecordsAsync(rawSignalsPath, ct))
        {
            if (!string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
                continue;
            signals.Add(ToFeatureSignal(record));
        }
        signals.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return signals;
    }

    public static FeatureSignal ToFeatureSignal(RawCollectorSignalRecord record)
    {
        SignalEventTypeParser.TryParse(record.SignalType, out var type);
        return new FeatureSignal(
            record.TimestampUtc,
            type,
            record.Payload,
            record.NativeAggregationSec);
    }
}
