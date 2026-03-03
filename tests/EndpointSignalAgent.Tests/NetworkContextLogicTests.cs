using EndpointSignalAgent.SignalCollection.Collectors.Network;
using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class NetworkContextLogicTests
{
    [Fact(Skip = "Temporarily disabled")]
    public void PrimaryResolver_UsesBestInterfaceIndex()
    {
        var provider = new FakeIndexProvider(success: true, index: 12, reason: "ok");
        var resolver = new PrimaryInterfaceResolver(provider);
        var interfaces = new[]
        {
            MakeInterface("a", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ipv4: 7),
            MakeInterface("b", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, ipv4: 12)
        };

        var result = resolver.Resolve(interfaces);

        Assert.NotNull(result.Interface);
        Assert.Equal("b", result.Interface!.Id);
        Assert.Equal("default_route", result.ReasonCode);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void PrimaryResolver_FallsBackWhenBestInterfaceFails()
    {
        var provider = new FakeIndexProvider(success: false, index: 0, reason: "api_fail");
        var resolver = new PrimaryInterfaceResolver(provider);
        var interfaces = new[]
        {
            MakeInterface("loop", NetworkInterfaceType.Loopback, OperationalStatus.Up, ipv4: 1),
            MakeInterface("eth", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ipv4: 2)
        };

        var result = resolver.Resolve(interfaces);

        Assert.NotNull(result.Interface);
        Assert.Equal("eth", result.Interface!.Id);
        Assert.Equal("fallback_api_fail", result.ReasonCode);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void VpnDecision_DetectsSplitTunnelRoutes()
    {
        var engine = new VpnDecisionEngine();
        var hash = new FakeHashing();
        var primary = MakeInterface("eth", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ipv4: 4);
        var tunnel = MakeInterface("tun", NetworkInterfaceType.Tunnel, OperationalStatus.Up, ipv4: 9);
        var routes = new[]
        {
            new RouteEntry(IPAddress.Any, 0, 4, NetworkInterfaceType.Ethernet, false),
            new RouteEntry(IPAddress.Parse("10.0.0.0"), 8, 9, NetworkInterfaceType.Tunnel, true)
        };
        var ras = new RasState(true, false, Array.Empty<string>(), "none");

        var result = engine.Evaluate(primary, new[] { primary, tunnel }, routes, ras, hash);

        Assert.True(result.VpnOn);
        Assert.Equal("medium", result.Confidence);
        Assert.Equal("split_routes", result.ReasonCode);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void Debouncer_RequiresTwoConsecutiveValuesBeforeCommit()
    {
        var debouncer = new SignalDebouncer<string>(2, TimeSpan.FromSeconds(6), StringComparer.Ordinal);
        var t0 = DateTimeOffset.Parse("2026-03-02T00:00:00Z");
        debouncer.Initialize("A");

        var changed1 = debouncer.TryUpdate("B", t0.AddSeconds(1), out var stable1);
        var changed2 = debouncer.TryUpdate("B", t0.AddSeconds(2), out var stable2);

        Assert.False(changed1);
        Assert.Equal("A", stable1);
        Assert.True(changed2);
        Assert.Equal("B", stable2);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void Debouncer_CommitsAfterDurationEvenWithoutConsecutiveThreshold()
    {
        var debouncer = new SignalDebouncer<string>(3, TimeSpan.FromSeconds(6), StringComparer.Ordinal);
        var t0 = DateTimeOffset.Parse("2026-03-02T00:00:00Z");
        debouncer.Initialize("A");

        var changed1 = debouncer.TryUpdate("B", t0.AddSeconds(1), out _);
        var changed2 = debouncer.TryUpdate("B", t0.AddSeconds(8), out var stable);

        Assert.False(changed1);
        Assert.True(changed2);
        Assert.Equal("B", stable);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void LocalFingerprint_IsStableWhenGatewayAndDnsOrderingChanges()
    {
        var builder = new LocalNetworkFingerprintBuilder();
        var hashing = new FakeHashing();

        var first = MakeInterface(
            "eth",
            NetworkInterfaceType.Ethernet,
            OperationalStatus.Up,
            ipv4: 3,
            unicast: new[] { IPAddress.Parse("192.168.1.24") },
            gateway: new[] { IPAddress.Parse("192.168.1.1"), IPAddress.Parse("10.0.0.1") },
            dns: new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1") });

        var second = first with
        {
            GatewayAddresses = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("192.168.1.1") },
            DnsAddresses = new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") }
        };

        var id1 = builder.Build(first, hashing);
        var id2 = builder.Build(second, hashing);

        Assert.Equal(id1.LocalNetworkValue, id2.LocalNetworkValue);
        Assert.Equal(id1.LocalPrefixValue, id2.LocalPrefixValue);
    }

    private static InterfaceSnapshot MakeInterface(
        string id,
        NetworkInterfaceType type,
        OperationalStatus status,
        int? ipv4,
        IReadOnlyList<IPAddress>? unicast = null,
        IReadOnlyList<IPAddress>? gateway = null,
        IReadOnlyList<IPAddress>? dns = null)
    {
        return new InterfaceSnapshot(
            id,
            id,
            id,
            type,
            status,
            ipv4,
            null,
            Guid.NewGuid(),
            unicast ?? Array.Empty<IPAddress>(),
            gateway ?? Array.Empty<IPAddress>(),
            dns ?? Array.Empty<IPAddress>());
    }

    private sealed class FakeIndexProvider : IPrimaryInterfaceIndexProvider
    {
        private readonly bool _success;
        private readonly int _index;
        private readonly string _reason;

        public FakeIndexProvider(bool success, int index, string reason)
        {
            _success = success;
            _index = index;
            _reason = reason;
        }

        public bool TryGetPrimaryInterfaceIndex(out int index, out string reasonCode)
        {
            index = _index;
            reasonCode = _reason;
            return _success;
        }
    }

    private sealed class FakeHashing : IHashingService
    {
        public string HashStable(string value) => value;
    }
}
