using System.Globalization;
using System.Text;
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.Services;

internal static class FeatureCsvRowSerializer
{
    private static readonly string[] MetaColumns =
    [
        "device_id", "window_start_ts", "window_sec", "slide_sec",
        "feature_schema_version", "extraction_run_id"
    ];

    public static readonly string Header =
        string.Join(",", MetaColumns.Concat(FeatureSchema.AllColumns));

    public static string SerializeRow(FeatureRow row)
    {
        var sb = new StringBuilder(512);

        AppendField(sb, row.DeviceId, first: true);
        AppendField(sb, row.WindowStartTs.ToString("O", CultureInfo.InvariantCulture));
        AppendField(sb, row.WindowSec.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, row.SlideSec.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, row.FeatureSchemaVersion);
        AppendField(sb, row.ExtractionRunId);

        foreach (var col in FeatureSchema.AllColumns)
        {
            var value = row.Features.TryGetValue(col, out var v)
                ? FormatFeatureValue(v)
                : "0";
            AppendField(sb, value);
        }

        return sb.ToString();
    }

    private static string FormatFeatureValue(object v) => v switch
    {
        double d  => d.ToString("G", CultureInfo.InvariantCulture),
        float  f  => f.ToString("G", CultureInfo.InvariantCulture),
        int    i  => i.ToString(CultureInfo.InvariantCulture),
        long   l  => l.ToString(CultureInfo.InvariantCulture),
        decimal dec => dec.ToString("G", CultureInfo.InvariantCulture),
        bool   b  => b ? "1" : "0",
        _         => v?.ToString() ?? "0"
    };

    private static void AppendField(StringBuilder sb, string value, bool first = false)
    {
        if (!first) sb.Append(',');

        if (value.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            sb.Append('"');
            sb.Append(value.Replace("\"", "\"\""));
            sb.Append('"');
        }
        else
        {
            sb.Append(value);
        }
    }
}
