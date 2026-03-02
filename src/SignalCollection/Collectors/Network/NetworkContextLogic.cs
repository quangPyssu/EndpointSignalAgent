using System.Net;
using System.Net.NetworkInformation;

namespace EndpointSignalAgent.SignalCollection.Collectors.Network;

internal sealed class SignalDebouncer<T>
{
    private readonly int _requiredConsecutive;
    private readonly TimeSpan _requiredDuration;
    private readonly IEqualityComparer<T> _comparer;

    private bool _initialized;
    private T? _stableValue;
    private T? _candidateValue;
    private int _candidateCount;
    private DateTimeOffset _candidateSinceUtc;

    public SignalDebouncer(int requiredConsecutive, TimeSpan requiredDuration, IEqualityComparer<T>? comparer = null)
    {
        _requiredConsecutive = Math.Max(1, requiredConsecutive);
        _requiredDuration = requiredDuration < TimeSpan.Zero ? TimeSpan.Zero : requiredDuration;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public T? StableValue => _stableValue;

    public void Initialize(T value)
    {
        _stableValue = value;
        _candidateValue = value;
        _candidateCount = _requiredConsecutive;
        _initialized = true;
    }

    public bool TryUpdate(T value, DateTimeOffset nowUtc, out T stableValue)
    {
        stableValue = value;
        if (!_initialized)
        {
            Initialize(value);
            stableValue = value;
            return true;
        }

        if (_comparer.Equals(value, _stableValue!))
        {
            _candidateValue = value;
            _candidateCount = _requiredConsecutive;
            _candidateSinceUtc = nowUtc;
            stableValue = _stableValue!;
            return false;
        }

        if (_candidateValue is null || !_comparer.Equals(value, _candidateValue))
        {
            _candidateValue = value;
            _candidateCount = 1;
            _candidateSinceUtc = nowUtc;
            stableValue = _stableValue!;
            return false;
        }

        _candidateCount++;
        if (_candidateCount >= _requiredConsecutive || (nowUtc - _candidateSinceUtc) >= _requiredDuration)
        {
            _stableValue = _candidateValue;
            stableValue = _stableValue!;
            return true;
        }

        stableValue = _stableValue!;
        return false;
    }
}

internal sealed class VpnDecisionEngine
{
    private static readonly string[] KnownVpnKeywords =
    {
        "vpn", "wireguard", "openvpn", "tunnel", "tap", "tunsafe", "nordlynx", "anyconnect", "fortinet", "globalprotect", "zscaler"
    };

    public VpnAssessment Evaluate(
        InterfaceSnapshot? primary,
        IReadOnlyList<InterfaceSnapshot> interfaces,
        IReadOnlyList<RouteEntry> routes,
        RasState rasState,
        IHashingService hashing)
    {
        if (primary is not null)
        {
            if (primary.InterfaceType == NetworkInterfaceType.Tunnel || primary.InterfaceType == NetworkInterfaceType.Ppp || IsKnownVpnAdapter(primary))
            {
                return new VpnAssessment(true, "high", "primary_tunnel", HashPrimary(primary, hashing));
            }
        }

        if (HasSplitTunnel(primary, routes))
        {
            var adapter = primary is null ? null : HashPrimary(primary, hashing);
            return new VpnAssessment(true, "medium", "split_routes", adapter);
        }

        if (rasState.HasActiveConnection)
        {
            return new VpnAssessment(true, rasState.ConnectionDeviceNames.Count > 0 ? "high" : "medium", "ras_active", HashRas(rasState, hashing));
        }

        var heuristic = interfaces
            .Where(i => i.Status == OperationalStatus.Up)
            .FirstOrDefault(IsKnownVpnAdapter);

        if (heuristic is not null)
        {
            return new VpnAssessment(true, "low", "heuristic_name", HashPrimary(heuristic, hashing));
        }

        return new VpnAssessment(false, "high", "none", null);
    }

    private static bool HasSplitTunnel(InterfaceSnapshot? primary, IReadOnlyList<RouteEntry> routes)
    {
        if (primary is null || routes.Count == 0)
        {
            return false;
        }

        var defaultRouteNotTunnel = routes
            .Where(x => x.PrefixAddress.Equals(IPAddress.Any) && x.PrefixLength == 0)
            .Any(x => x.InterfaceIndex == primary.IPv4Index && !x.IsTunnelLike);

        if (!defaultRouteNotTunnel)
        {
            return false;
        }

        return routes.Any(r =>
            r.IsTunnelLike &&
            IsPrivatePrefix(r.PrefixAddress, r.PrefixLength));
    }

    private static bool IsPrivatePrefix(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        if (b[0] == 10 && prefixLength <= 8) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31 && prefixLength <= 12) return true;
        if (b[0] == 192 && b[1] == 168 && prefixLength <= 16) return true;
        return false;
    }

    private static bool IsKnownVpnAdapter(InterfaceSnapshot i)
    {
        var text = $"{i.Name} {i.Description}".ToLowerInvariant();
        return KnownVpnKeywords.Any(text.Contains);
    }

    private static string HashPrimary(InterfaceSnapshot snapshot, IHashingService hashing)
    {
        return hashing.HashStable($"vpn|{snapshot.Id}|{snapshot.Name}|{snapshot.Description}");
    }

    private static string? HashRas(RasState rasState, IHashingService hashing)
    {
        if (rasState.ConnectionDeviceNames.Count == 0)
        {
            return null;
        }

        var joined = string.Join("|", rasState.ConnectionDeviceNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return hashing.HashStable($"ras|{joined}");
    }
}

internal sealed class LocalNetworkFingerprintBuilder
{
    public LocalNetworkIdentity Build(InterfaceSnapshot? primary, IHashingService hashing)
    {
        if (primary is null || primary.Status != OperationalStatus.Up)
        {
            return new LocalNetworkIdentity("none", "none", "none", "no_connectivity");
        }

        var ipv4 = primary.UnicastAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var ipv6 = primary.UnicastAddresses
            .Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            .FirstOrDefault(x => !x.IsIPv6LinkLocal && !x.IsIPv6Multicast);

        string family;
        string prefixMaterial;
        if (ipv4 is not null)
        {
            family = "v4";
            var b = ipv4.GetAddressBytes();
            prefixMaterial = $"v4|{b[0]}.{b[1]}.{b[2]}.0/24";
        }
        else if (ipv6 is not null)
        {
            family = "v6";
            var b = ipv6.GetAddressBytes();
            ushort h0 = (ushort)((b[0] << 8) | b[1]);
            ushort h1 = (ushort)((b[2] << 8) | b[3]);
            ushort h2 = (ushort)((b[4] << 8) | b[5]);
            ushort h3 = (ushort)((b[6] << 8) | b[7]);
            prefixMaterial = $"v6|{h0:x4}:{h1:x4}:{h2:x4}:{h3:x4}::/64";
        }
        else
        {
            return new LocalNetworkIdentity("unknown", "unknown", "none", "no_unicast");
        }

        var gatewaySorted = primary.GatewayAddresses
            .Select(x => x.ToString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var dnsSorted = primary.DnsAddresses
            .Select(x => x.ToString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var gatewayPart = gatewaySorted.Length == 0 ? "none" : string.Join(",", gatewaySorted);
        var dnsPart = dnsSorted.Length == 0 ? "none" : string.Join(",", dnsSorted);

        var prefixHash = hashing.HashStable($"local_prefix|{prefixMaterial}");
        var compositeHash = hashing.HashStable($"local_network|{prefixMaterial}|gw:{gatewayPart}|dns:{dnsPart}");
        return new LocalNetworkIdentity(prefixHash, compositeHash, family, "ok");
    }
}
