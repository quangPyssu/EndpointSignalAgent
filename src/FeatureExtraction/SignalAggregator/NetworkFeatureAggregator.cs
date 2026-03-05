using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

/// <summary>
/// Environment/network features sourced only from NetworkContextCollector signals.
/// </summary>
internal sealed class NetworkFeatureAggregator
{
    public NetworkFeatureResult ExtractFeatures(
        IReadOnlyList<FeatureSignal> events,
        SlidingWindow window)
    {
        var features = FeatureSchema.NetworkColumns.ToDictionary(column => column, _ => 0.0, StringComparer.Ordinal);

        var netEvents = events
            .Where(IsNetworkSignal)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        var inWindow = netEvents
            .Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc)
            .ToList();

        features["vpn_flip_count"] = inWindow.Count(e => e.Type == SignalEventType.VpnStateChanged);
        features["wifi_flip_count"] = inWindow.Count(e => e.Type == SignalEventType.WifiLinkChanged);
        features["local_network_change_count"] = inWindow.Count(e => e.Type == SignalEventType.LocalNetworkChanged);
        features["public_ip_bucket_change_count"] = inWindow.Count(e => e.Type == SignalEventType.PublicIpBucketChanged);
        features["local_prefix_change_count"] = features["local_network_change_count"];

        var uniqueSsid = new HashSet<string>(StringComparer.Ordinal);
        var uniqueBssid = new HashSet<string>(StringComparer.Ordinal);
        var uniqueLocal = new HashSet<string>(StringComparer.Ordinal);
        var uniquePublic = new HashSet<string>(StringComparer.Ordinal);

        var ssidChangeCount = 0;
        string? previousSsid = null;

        foreach (var evt in inWindow)
        {
            if (evt.Type == SignalEventType.WifiSsidChanged)
            {
                var ssid = PayloadValueReader.GetString(evt.Payload, "wifiSsid", "none");
                var bssid = PayloadValueReader.GetString(evt.Payload, "wifiBssidHash", "none");

                if (!string.IsNullOrWhiteSpace(ssid) && ssid is not "none" and not "unknown")
                {
                    uniqueSsid.Add(ssid);
                }

                if (!string.IsNullOrWhiteSpace(bssid) && bssid is not "none" and not "unknown")
                {
                    uniqueBssid.Add(bssid);
                }

                if (previousSsid is null || !string.Equals(previousSsid, ssid, StringComparison.Ordinal))
                {
                    ssidChangeCount++;
                }

                previousSsid = ssid;
            }

            if (evt.Type == SignalEventType.LocalNetworkChanged)
            {
                var local = PayloadValueReader.GetString(evt.Payload, "localNetworkHash");
                if (!string.IsNullOrWhiteSpace(local) && local is not "none" and not "unknown")
                {
                    uniqueLocal.Add(local);
                }
            }

            if (evt.Type == SignalEventType.PublicIpBucketChanged)
            {
                var bucket = PayloadValueReader.GetString(evt.Payload, "publicIpBucket");
                if (!string.IsNullOrWhiteSpace(bucket) && bucket is not "none" and not "unknown")
                {
                    uniquePublic.Add(bucket);
                }
            }
        }

        features["ssid_change_count"] = ssidChangeCount;
        features["unique_wifi_ssid_count"] = uniqueSsid.Count;
        features["unique_wifi_bssid_count"] = uniqueBssid.Count;
        features["unique_local_network_count"] = uniqueLocal.Count;
        features["unique_public_bucket_count"] = uniquePublic.Count;

        features["public_ip_fetch_fail_count"] = inWindow.Count(e =>
            e.Type == SignalEventType.PublicIpBucketChanged &&
            string.Equals(PayloadValueReader.GetString(e.Payload, "publicIpFetchStatus"), "fail", StringComparison.OrdinalIgnoreCase));

        var state = BuildInitialState(netEvents, window.StartUtc);
        var cursor = window.StartUtc;

        long vpnOnMs = 0;
        long wifiConnectedMs = 0;
        long publicKnownMs = 0;
        long publicBackoffMs = 0;

        foreach (var evt in netEvents.Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc))
        {
            if (evt.TimestampUtc > cursor)
            {
                var delta = (long)(evt.TimestampUtc - cursor).TotalMilliseconds;
                if (state.VpnOn)
                {
                    vpnOnMs += delta;
                }

                if (state.WifiUp)
                {
                    wifiConnectedMs += delta;
                }

                if (state.PublicIpKnown)
                {
                    publicKnownMs += delta;
                }

                if (state.PublicIpBackoff)
                {
                    publicBackoffMs += delta;
                }

                cursor = evt.TimestampUtc;
            }

            ApplyTransition(evt, ref state);
        }

        if (cursor < window.EndUtc)
        {
            var delta = (long)(window.EndUtc - cursor).TotalMilliseconds;
            if (state.VpnOn)
            {
                vpnOnMs += delta;
            }

            if (state.WifiUp)
            {
                wifiConnectedMs += delta;
            }

            if (state.PublicIpKnown)
            {
                publicKnownMs += delta;
            }

            if (state.PublicIpBackoff)
            {
                publicBackoffMs += delta;
            }
        }

        var windowMs = Math.Max(1L, window.DurationMs);
        features["vpn_on_ratio"] = FeatureMath.SafeDivide(vpnOnMs, windowMs);
        features["primary_wifi_connected_ratio"] = FeatureMath.SafeDivide(wifiConnectedMs, windowMs);
        features["wifi_up_ratio"] = features["primary_wifi_connected_ratio"];
        features["public_ip_known_ratio"] = FeatureMath.SafeDivide(publicKnownMs, windowMs);
        features["public_ip_backoff_ratio"] = FeatureMath.SafeDivide(publicBackoffMs, windowMs);

        features["has_net_data"] = inWindow.Count > 0 ? 1.0 : 0.0;

        return new NetworkFeatureResult(features);
    }

    private static bool IsNetworkSignal(FeatureSignal signal)
    {
        return signal.Type is SignalEventType.VpnStateChanged or
            SignalEventType.WifiLinkChanged or
            SignalEventType.WifiSsidChanged or
            SignalEventType.LocalNetworkChanged or
            SignalEventType.PublicIpBucketChanged;
    }

    private static NetworkState BuildInitialState(IReadOnlyList<FeatureSignal> orderedEvents, DateTimeOffset windowStart)
    {
        var state = NetworkState.Default;
        foreach (var evt in orderedEvents)
        {
            if (evt.TimestampUtc >= windowStart)
            {
                break;
            }

            ApplyTransition(evt, ref state);
        }

        return state;
    }

    private static void ApplyTransition(FeatureSignal evt, ref NetworkState state)
    {
        switch (evt.Type)
        {
            case SignalEventType.VpnStateChanged:
                if (PayloadValueReader.TryGetBool(evt.Payload, "vpnOn", out var vpnOn))
                {
                    state.VpnOn = vpnOn;
                }
                break;

            case SignalEventType.WifiLinkChanged:
            case SignalEventType.WifiSsidChanged:
                if (PayloadValueReader.TryGetBool(evt.Payload, "wifiUp", out var wifiUp))
                {
                    state.WifiUp = wifiUp;
                }
                break;

            case SignalEventType.PublicIpBucketChanged:
                var status = PayloadValueReader.GetString(evt.Payload, "publicIpFetchStatus", "fail");
                var bucket = PayloadValueReader.GetString(evt.Payload, "publicIpBucket", "none");
                state.PublicIpKnown = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) &&
                    bucket is not "none" and not "unknown" && !string.IsNullOrWhiteSpace(bucket);
                state.PublicIpBackoff = string.Equals(status, "backoff", StringComparison.OrdinalIgnoreCase);
                break;
        }
    }

    private struct NetworkState
    {
        public bool VpnOn;
        public bool WifiUp;
        public bool PublicIpKnown;
        public bool PublicIpBackoff;

        public static NetworkState Default => new()
        {
            VpnOn = false,
            WifiUp = false,
            PublicIpKnown = false,
            PublicIpBackoff = false
        };
    }
}
