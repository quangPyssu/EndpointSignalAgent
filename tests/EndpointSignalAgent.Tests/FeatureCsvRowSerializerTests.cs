using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Services;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class FeatureCsvRowSerializerTests
{
    /// <summary>
    /// Splits a single CSV line respecting RFC 4180 quoting.
    /// Only handles unescaped fields and fully-quoted fields (no mixed content).
    /// </summary>
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }
            if (line[i] == '"')
            {
                // Quoted field
                i++; // skip opening quote
                var sb = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; } // closing quote
                    }
                    else { sb.Append(line[i]); i++; }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++; // skip comma
            }
            else
            {
                // Unquoted field
                var end = line.IndexOf(',', i);
                if (end < 0) { fields.Add(line.Substring(i)); break; }
                fields.Add(line.Substring(i, end - i));
                i = end + 1;
            }
        }
        return fields.ToArray();
    }

    private static FeatureRow MakeRow(Dictionary<string, object>? features = null) =>
        new(
            Id: 42,
            DeviceId: "dev-001",
            WindowSec: 60,
            WindowStartTs: DateTimeOffset.Parse("2026-06-08T10:00:00Z"),
            FeatureVersion: "1.2",
            WindowProfileId: "W60_S30",
            WindowSizeSec: 60,
            SlideSec: 30,
            EventTimeStart: DateTimeOffset.Parse("2026-06-08T10:00:00Z"),
            EventTimeEnd: DateTimeOffset.Parse("2026-06-08T10:01:00Z"),
            ExtractionRunId: "run-abc",
            FeatureSchemaVersion: "1.2",
            CollectorSchemaVersion: null,
            SourceCounts: new Dictionary<string, int>(),
            Features: features ?? new Dictionary<string, object>(),
            SentFlag: false,
            SentAt: null);

    [Fact]
    public void Header_HasExact104Columns()
    {
        var header = FeatureCsvRowSerializer.Header;
        var cols = header.Split(',');
        Assert.Equal(104, cols.Length);
    }

    [Fact]
    public void Header_StartsWithMetadataColumns()
    {
        var cols = FeatureCsvRowSerializer.Header.Split(',');
        Assert.Equal("device_id", cols[0]);
        Assert.Equal("window_start_ts", cols[1]);
        Assert.Equal("window_sec", cols[2]);
        Assert.Equal("slide_sec", cols[3]);
        Assert.Equal("feature_schema_version", cols[4]);
        Assert.Equal("extraction_run_id", cols[5]);
    }

    [Fact]
    public void Header_FeatureColumnsMatchAllColumnsOrder()
    {
        var cols = FeatureCsvRowSerializer.Header.Split(',');
        var featureCols = cols.Skip(6).ToArray();
        Assert.Equal(FeatureSchema.AllColumns, featureCols);
    }

    [Fact]
    public void SerializeRow_MetadataFieldsCorrect()
    {
        var row = MakeRow();
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        var cols = SplitCsvLine(line);

        Assert.Equal("dev-001", cols[0]);
        Assert.Equal("2026-06-08T10:00:00.0000000+00:00", cols[1]);
        Assert.Equal("60", cols[2]);
        Assert.Equal("30", cols[3]);
        Assert.Equal("1.2", cols[4]);
        Assert.Equal("run-abc", cols[5]);
    }

    [Fact]
    public void SerializeRow_MissingFeatureBecomesZero()
    {
        var row = MakeRow(features: new Dictionary<string, object>());
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        var cols = SplitCsvLine(line);
        foreach (var col in cols.Skip(6))
            Assert.Equal("0", col);
    }

    [Fact]
    public void SerializeRow_FeatureValueWrittenInCorrectPosition()
    {
        var features = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["app_switch_count"] = 7.0
        };
        var row = MakeRow(features);
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        var cols = SplitCsvLine(line);
        Assert.Equal("7", cols[6]);
    }

    [Fact]
    public void SerializeRow_DeviceIdWithCommaIsQuoted()
    {
        var row = MakeRow() with { DeviceId = "dev,001" };
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        Assert.StartsWith("\"dev,001\"", line);
    }
}
