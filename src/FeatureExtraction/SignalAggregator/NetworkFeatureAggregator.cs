using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.src.FeatureExtraction.SignalAggregator;

/// <summary>
/// Aggregates network-related features from signal events based on the network_window_features schema.
/// Handles VPN, WiFi, SSID, local network, and public IP changes.
/// </summary>
public sealed class NetworkFeatureAggregator
{
    /// <summary>
    /// Extract network features from events in a time window.
    /// Uses state integration for VPN and WiFi ratios, and event counting for changes.
    /// </summary>
    public Dictionary<string, object> ExtractFeatures(
        List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> events,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var features = new Dictionary<string, object>();
        var windowDurationMs = (windowEnd - windowStart).TotalMilliseconds;

        // Filter relevant events and sort by timestamp
        var networkEvents = events
            .Where(e => e.Type == SignalEventType.VpnStateChanged ||
                       e.Type == SignalEventType.WifiLinkChanged ||
                       e.Type == SignalEventType.WifiSsidChanged ||
                       e.Type == SignalEventType.LocalNetworkChanged ||
                       e.Type == SignalEventType.PublicIpBucketChanged)
            .OrderBy(e => e.Timestamp)
            .ToList();

        // Data quality indicator
        var inWindow = events.Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd).ToList();

        features["has_net_data"] = inWindow.Any(e =>
            e.Type == SignalEventType.VpnStateChanged ||
            e.Type == SignalEventType.WifiLinkChanged ||
            e.Type == SignalEventType.WifiSsidChanged ||
            e.Type == SignalEventType.LocalNetworkChanged ||
            e.Type == SignalEventType.PublicIpBucketChanged) ? 1 : 0;

        features["vpn_flip_count"] = inWindow.Count(e => e.Type == SignalEventType.VpnStateChanged);
        features["wifi_flip_count"] = inWindow.Count(e => e.Type == SignalEventType.WifiLinkChanged);
        features["ssid_change_count"] = inWindow.Count(e => e.Type == SignalEventType.WifiSsidChanged);
        features["local_prefix_change_count"] = inWindow.Count(e => e.Type == SignalEventType.LocalNetworkChanged);
        features["public_ip_bucket_change_count"] = inWindow.Count(e => e.Type == SignalEventType.PublicIpBucketChanged);


        // State integration for VPN and WiFi
        bool vpnOn = false;
        bool wifiUp = false;

        long vpnOnTimeMs = 0;
        long wifiUpTimeMs = 0;

        DateTimeOffset lastTs = windowStart;

        foreach (var evt in networkEvents)
        {
            // Skip events before window start
            if (evt.Timestamp < windowStart)
                continue;

            // Process interval from lastTs to current event timestamp
            var intervalStart = lastTs;
            var intervalEnd = evt.Timestamp > windowEnd ? windowEnd : evt.Timestamp;
            var intervalMs = (long)(intervalEnd - intervalStart).TotalMilliseconds;

            if (intervalMs > 0)
            {
                if (vpnOn)
                    vpnOnTimeMs += intervalMs;
                if (wifiUp)
                    wifiUpTimeMs += intervalMs;
            }

            // Update state based on event type
            switch (evt.Type)
            {
                case SignalEventType.VpnStateChanged:
                    if (evt.Payload.TryGetValue("vpnOn", out var vpnOnStr) &&
                        bool.TryParse(vpnOnStr, out var vpnState))
                    {
                        vpnOn = vpnState;
                    }
                    break;

                case SignalEventType.WifiLinkChanged:
                case SignalEventType.WifiSsidChanged:
                    if (evt.Payload.TryGetValue("wifiUp", out var wifiUpStr) &&
                        bool.TryParse(wifiUpStr, out var wifiState))
                    {
                        wifiUp = wifiState;
                    }
                    break;
            }

            lastTs = intervalEnd;

            // If we've reached window end, stop processing
            if (intervalEnd >= windowEnd)
                break;
        }

        // Process final interval from lastTs to windowEnd
        if (lastTs < windowEnd)
        {
            var finalIntervalMs = (long)(windowEnd - lastTs).TotalMilliseconds;

            if (finalIntervalMs > 0)
            {
                if (vpnOn)
                    vpnOnTimeMs += finalIntervalMs;
                if (wifiUp)
                    wifiUpTimeMs += finalIntervalMs;
            }
        }

        // Compute ratios
        features["vpn_on_ratio"] = windowDurationMs > 0 ? vpnOnTimeMs / windowDurationMs : 0.0;
        features["wifi_up_ratio"] = windowDurationMs > 0 ? wifiUpTimeMs / windowDurationMs : 0.0;

        return features;
    }
}
