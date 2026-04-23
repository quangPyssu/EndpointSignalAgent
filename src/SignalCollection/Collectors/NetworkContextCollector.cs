using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Services;
using EndpointSignalAgent.SignalCollection.Collectors.Network;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;

namespace EndpointSignalAgent.SignalCollection.Collectors;

/// <summary>
/// Collects network context with privacy-preserving hashed outputs and change-only emission.
/// </summary>
public sealed class NetworkContextCollector : SignalCollectorBase
{
    private readonly ILogger<NetworkContextCollector> _logger;
    private readonly INetworkInterfaceSnapshotProvider _interfaceSnapshotProvider;
    private readonly IPrimaryInterfaceResolver _primaryInterfaceResolver;
    private readonly IRouteTableReader _routeTableReader;
    private readonly IRasReader _rasReader;
    private readonly IWlanReader _wlanReader;
    private readonly IHashingService _hashing;
    private readonly IClock _clock;
    private readonly VpnDecisionEngine _vpnEngine;
    private readonly LocalNetworkFingerprintBuilder _localNetworkBuilder;

    private readonly TimeSpan _poll = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _publicIpPoll = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _debounceWindow = TimeSpan.FromSeconds(6);
    private readonly int _debounceConsecutive = 2;

    private readonly SignalDebouncer<bool> _vpnDebouncer;
    private readonly SignalDebouncer<bool> _wifiUpDebouncer;
    private readonly SignalDebouncer<string> _wifiSsidDebouncer;
    private readonly SignalDebouncer<string> _wifiBssidDebouncer;
    private readonly SignalDebouncer<string> _localNetworkDebouncer;
    private readonly SignalDebouncer<string> _publicIpDebouncer;

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly IReadOnlyList<IPublicIpProvider> _publicIpProviders;
    private int _publicProviderIndex;
    private int _publicFailureCount;
    private DateTimeOffset _nextPublicIpAttemptUtc = DateTimeOffset.MinValue;
    private PublicIpSample _publicIpSample = new("fail", null, null);

    private readonly Dictionary<string, long> _healthCounters = new(StringComparer.Ordinal)
    {
        ["wlan_success"] = 0,
        ["wlan_fail"] = 0,
        ["ras_success"] = 0,
        ["ras_fail"] = 0,
        ["route_read_success"] = 0,
        ["route_read_fail"] = 0,
        ["public_ip_success"] = 0,
        ["public_ip_fail"] = 0
    };

    public NetworkContextCollector(
        ILogger<NetworkContextCollector> logger,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl)
        : this(
            logger,
            broadcaster,
            collectionControl,
            interfaceSnapshotProvider: null,
            primaryInterfaceResolver: null,
            routeTableReader: null,
            rasReader: null,
            wlanReader: null,
            hashing: null,
            clock: null,
            vpnEngine: null,
            localNetworkBuilder: null,
            publicIpProviders: null)
    {
    }

    internal NetworkContextCollector(
        ILogger<NetworkContextCollector> logger,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl,
        INetworkInterfaceSnapshotProvider? interfaceSnapshotProvider,
        IPrimaryInterfaceResolver? primaryInterfaceResolver,
        IRouteTableReader? routeTableReader,
        IRasReader? rasReader,
        IWlanReader? wlanReader,
        IHashingService? hashing,
        IClock? clock,
        VpnDecisionEngine? vpnEngine,
        LocalNetworkFingerprintBuilder? localNetworkBuilder,
        IReadOnlyList<IPublicIpProvider>? publicIpProviders)
        : base(@"spool\signals.jsonl", broadcaster, collectionControl)
    {
        _logger = logger;
        _interfaceSnapshotProvider = interfaceSnapshotProvider ?? new NetworkInterfaceSnapshotProvider();
        _primaryInterfaceResolver = primaryInterfaceResolver ?? new PrimaryInterfaceResolver(new WindowsPrimaryInterfaceIndexProvider());
        _routeTableReader = routeTableReader ?? new WindowsRouteTableReader();
        _rasReader = rasReader ?? new WindowsRasReader();
        _wlanReader = wlanReader ?? new WindowsWlanReader();
        _hashing = hashing ?? new HashingService(new StableSaltProvider());
        _clock = clock ?? new SystemClock();
        _vpnEngine = vpnEngine ?? new VpnDecisionEngine();
        _localNetworkBuilder = localNetworkBuilder ?? new LocalNetworkFingerprintBuilder();

        _publicIpProviders = publicIpProviders ?? new List<IPublicIpProvider>
        {
            new HttpPublicIpProvider("ipify", "https://api.ipify.org", SharedHttpClient),
            new HttpPublicIpProvider("ifconfig.me", "https://ifconfig.me/ip", SharedHttpClient)
        };

        _vpnDebouncer = new SignalDebouncer<bool>(_debounceConsecutive, _debounceWindow);
        _wifiUpDebouncer = new SignalDebouncer<bool>(_debounceConsecutive, _debounceWindow);
        _wifiSsidDebouncer = new SignalDebouncer<string>(_debounceConsecutive, _debounceWindow, StringComparer.Ordinal);
        _wifiBssidDebouncer = new SignalDebouncer<string>(_debounceConsecutive, _debounceWindow, StringComparer.Ordinal);
        _localNetworkDebouncer = new SignalDebouncer<string>(_debounceConsecutive, _debounceWindow, StringComparer.Ordinal);
        _publicIpDebouncer = new SignalDebouncer<string>(_debounceConsecutive, _debounceWindow, StringComparer.Ordinal);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");
        _logger.LogInformation("NetworkContextCollector started.");

        try
        {
            await RefreshPublicIpAsync(stoppingToken, force: true);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("NetworkContextCollector initial public IP refresh timed out; continuing with fail status.");
        }
        var firstTick = BuildTickState(_clock.UtcNow);
        InitializeDebouncers(firstTick);
        await EmitInitialStateAsync(firstTick);

        using var timer = new PeriodicTimer(_poll);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = _clock.UtcNow;
                await RefreshPublicIpAsync(stoppingToken, force: false);
                var tick = BuildTickState(now);
                await ProcessTickAsync(tick, now);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NetworkContextCollector loop error.");
            }
        }

        _logger.LogInformation("NetworkContextCollector stopped.");
    }

    private NetworkTickState BuildTickState(DateTimeOffset nowUtc)
    {
        var interfaces = _interfaceSnapshotProvider.GetInterfaces();
        var primary = _primaryInterfaceResolver.Resolve(interfaces);

        IReadOnlyList<RouteEntry> routes;
        string routeReason;
        if (_routeTableReader.TryReadRoutes(interfaces, out routes, out routeReason))
        {
            IncrementCounter("route_read_success");
        }
        else
        {
            IncrementCounter("route_read_fail");
            routes = Array.Empty<RouteEntry>();
        }

        var ras = _rasReader.Read();
        IncrementCounter(ras.ApiAvailable ? "ras_success" : "ras_fail");

        var vpn = _vpnEngine.Evaluate(primary.Interface, interfaces, routes, ras, _hashing);
        var wifi = BuildWifiIdentity(primary.Interface);
        var local = _localNetworkBuilder.Build(primary.Interface, _hashing);

        var publicBucket = _publicIpSample.BucketHash ?? "none";
        var publicAgeSeconds = _publicIpSample.LastSuccessUtc.HasValue
            ? Math.Max(0, (int)(nowUtc - _publicIpSample.LastSuccessUtc.Value).TotalSeconds)
            : -1;

        return new NetworkTickState(
            primary.Interface,
            vpn,
            wifi,
            local,
            routeReason,
            _publicIpSample.Status,
            publicBucket,
            publicAgeSeconds);
    }

    private WifiIdentityState BuildWifiIdentity(InterfaceSnapshot? primary)
    {
        // reason codes:
        // not_wifi_primary: primary path is not Wi-Fi.
        // api_fail: wlan API unavailable/failed.
        // disconnected: Wi-Fi primary not currently associated.
        // connected: Wi-Fi identity from WLAN API.
        if (primary is null || primary.Status != OperationalStatus.Up)
        {
            return new WifiIdentityState(false, "none", "none", "low", "no_primary");
        }

        if (primary.InterfaceType != NetworkInterfaceType.Wireless80211)
        {
            return new WifiIdentityState(false, "none", "none", "low", "not_wifi_primary");
        }

        if (!primary.AdapterGuid.HasValue)
        {
            IncrementCounter("wlan_fail");
            return new WifiIdentityState(true, "unknown", "unknown", "low", "api_fail");
        }

        var wlan = _wlanReader.ReadByInterface(primary.AdapterGuid.Value);
        if (!wlan.ApiAvailable)
        {
            IncrementCounter("wlan_fail");
            return new WifiIdentityState(true, "unknown", "unknown", "low", "api_fail");
        }

        if (!wlan.Connected)
        {
            IncrementCounter("wlan_success");
            return new WifiIdentityState(false, "none", "none", "medium", "disconnected");
        }

        IncrementCounter("wlan_success");
        var ssid = string.IsNullOrWhiteSpace(wlan.Ssid) ? "unknown" : _hashing.HashStable($"ssid|{wlan.Ssid!.Trim()}");
        var bssid = string.IsNullOrWhiteSpace(wlan.Bssid) ? "none" : _hashing.HashStable($"bssid|{wlan.Bssid!.Trim()}");
        return new WifiIdentityState(true, ssid, bssid, "high", "connected");
    }

    private async Task RefreshPublicIpAsync(CancellationToken ct, bool force)
    {
        var now = _clock.UtcNow;
        if (!force && now < _nextPublicIpAttemptUtc)
        {
            _publicIpSample = _publicIpSample with { Status = "backoff" };
            return;
        }

        if (_publicIpProviders.Count == 0)
        {
            _publicIpSample = _publicIpSample with { Status = "fail" };
            return;
        }

        var provider = _publicIpProviders[_publicProviderIndex % _publicIpProviders.Count];
        try
        {
            var ip = await provider.TryGetPublicIpAsync(ct);
            if (ip is null)
            {
                IncrementCounter("public_ip_fail");
                MarkPublicFailure(now);
                return;
            }

            var bucket = ComputeIpBucket(ip);
            var bucketHash = _hashing.HashStable($"pub|{bucket}");
            _publicIpSample = new PublicIpSample("ok", bucketHash, now);
            _publicFailureCount = 0;
            _nextPublicIpAttemptUtc = now + _publicIpPoll;
            IncrementCounter("public_ip_success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            IncrementCounter("public_ip_fail");
            MarkPublicFailure(now);
        }
        catch
        {
            IncrementCounter("public_ip_fail");
            MarkPublicFailure(now);
        }
    }

    private void MarkPublicFailure(DateTimeOffset now)
    {
        _publicFailureCount++;
        _publicProviderIndex = (_publicProviderIndex + 1) % _publicIpProviders.Count;
        var backoffSeconds = Math.Min(300, (int)Math.Pow(2, Math.Min(_publicFailureCount, 7)));
        _nextPublicIpAttemptUtc = now + TimeSpan.FromSeconds(backoffSeconds);
        _publicIpSample = _publicIpSample with { Status = "fail" };
    }

    private void InitializeDebouncers(NetworkTickState state)
    {
        _vpnDebouncer.Initialize(state.Vpn.VpnOn);
        _wifiUpDebouncer.Initialize(state.Wifi.WifiUp);
        _wifiSsidDebouncer.Initialize(state.Wifi.WifiSsidValue);
        _wifiBssidDebouncer.Initialize(state.Wifi.WifiBssidValue);
        _localNetworkDebouncer.Initialize(state.Local.LocalNetworkValue);
        _publicIpDebouncer.Initialize(state.PublicIpBucketValue);
    }

    private async Task EmitInitialStateAsync(NetworkTickState state)
    {
        await WriteSignalAsync(SignalEventType.VpnStateChanged, new Dictionary<string, string>
        {
            ["vpnOn"] = state.Vpn.VpnOn ? "true" : "false",
            ["vpnAdapter"] = state.Vpn.AdapterFingerprint ?? "none",
            ["vpnConfidence"] = state.Vpn.Confidence,
            ["vpnReason"] = state.Vpn.ReasonCode,
            ["initial"] = "true"
        });

        await WriteSignalAsync(SignalEventType.WifiLinkChanged, new Dictionary<string, string>
        {
            ["wifiUp"] = state.Wifi.WifiUp ? "true" : "false",
            ["wifiIdentityConfidence"] = state.Wifi.Confidence,
            ["wifiIdentityReason"] = state.Wifi.ReasonCode,
            ["initial"] = "true"
        });

        await WriteSignalAsync(SignalEventType.WifiSsidChanged, new Dictionary<string, string>
        {
            ["wifiSsid"] = state.Wifi.WifiSsidValue,
            ["wifiUp"] = state.Wifi.WifiUp ? "true" : "false",
            ["wifiBssidHash"] = state.Wifi.WifiBssidValue,
            ["wifiIdentityConfidence"] = state.Wifi.Confidence,
            ["wifiIdentityReason"] = state.Wifi.ReasonCode,
            ["initial"] = "true"
        });

        await WriteSignalAsync(SignalEventType.LocalNetworkChanged, new Dictionary<string, string>
        {
            ["localPrefix"] = state.Local.LocalPrefixValue,
            ["localNetworkHash"] = state.Local.LocalNetworkValue,
            ["localIpFamily"] = state.Local.IpFamily,
            ["localPrefixHash"] = state.Local.LocalPrefixValue,
            ["localNetworkReason"] = state.Local.ReasonCode,
            ["initial"] = "true"
        });

        await WriteSignalAsync(SignalEventType.PublicIpBucketChanged, new Dictionary<string, string>
        {
            ["publicIpBucket"] = state.PublicIpBucketValue,
            ["publicIpAgeSeconds"] = state.PublicIpAgeSeconds.ToString(),
            ["publicIpFetchStatus"] = state.PublicIpFetchStatus,
            ["initial"] = "true"
        });
    }

    private async Task ProcessTickAsync(NetworkTickState state, DateTimeOffset nowUtc)
    {
        if (_vpnDebouncer.TryUpdate(state.Vpn.VpnOn, nowUtc, out var stableVpn))
        {
            await WriteSignalAsync(SignalEventType.VpnStateChanged, new Dictionary<string, string>
            {
                ["vpnOn"] = stableVpn ? "true" : "false",
                ["vpnAdapter"] = state.Vpn.AdapterFingerprint ?? "none",
                ["vpnConfidence"] = state.Vpn.Confidence,
                ["vpnReason"] = state.Vpn.ReasonCode
            });
        }

        if (_wifiUpDebouncer.TryUpdate(state.Wifi.WifiUp, nowUtc, out var stableWifiUp))
        {
            await WriteSignalAsync(SignalEventType.WifiLinkChanged, new Dictionary<string, string>
            {
                ["wifiUp"] = stableWifiUp ? "true" : "false",
                ["wifiIdentityConfidence"] = state.Wifi.Confidence,
                ["wifiIdentityReason"] = state.Wifi.ReasonCode
            });
        }

        var ssidChanged = _wifiSsidDebouncer.TryUpdate(state.Wifi.WifiSsidValue, nowUtc, out var stableSsid);
        var bssidChanged = _wifiBssidDebouncer.TryUpdate(state.Wifi.WifiBssidValue, nowUtc, out var stableBssid);
        if (ssidChanged || bssidChanged)
        {
            await WriteSignalAsync(SignalEventType.WifiSsidChanged, new Dictionary<string, string>
            {
                ["wifiSsid"] = stableSsid,
                ["wifiUp"] = _wifiUpDebouncer.StableValue == true ? "true" : "false",
                ["wifiBssidHash"] = stableBssid,
                ["wifiIdentityConfidence"] = state.Wifi.Confidence,
                ["wifiIdentityReason"] = state.Wifi.ReasonCode
            });
        }

        if (_localNetworkDebouncer.TryUpdate(state.Local.LocalNetworkValue, nowUtc, out var stableLocalNetwork))
        {
            await WriteSignalAsync(SignalEventType.LocalNetworkChanged, new Dictionary<string, string>
            {
                ["localPrefix"] = state.Local.LocalPrefixValue,
                ["localNetworkHash"] = stableLocalNetwork,
                ["localIpFamily"] = state.Local.IpFamily,
                ["localPrefixHash"] = state.Local.LocalPrefixValue,
                ["localNetworkReason"] = state.Local.ReasonCode
            });
        }

        if (_publicIpDebouncer.TryUpdate(state.PublicIpBucketValue, nowUtc, out var stablePublicBucket))
        {
            await WriteSignalAsync(SignalEventType.PublicIpBucketChanged, new Dictionary<string, string>
            {
                ["publicIpBucket"] = stablePublicBucket,
                ["publicIpAgeSeconds"] = state.PublicIpAgeSeconds.ToString(),
                ["publicIpFetchStatus"] = state.PublicIpFetchStatus
            });
        }
    }

    private void IncrementCounter(string key)
    {
        if (_healthCounters.TryGetValue(key, out var value))
        {
            _healthCounters[key] = value + 1;
            return;
        }

        _healthCounters[key] = 1;
    }

    private static string ComputeIpBucket(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return $"v4|{b[0]}.{b[1]}.{b[2]}.0/24";
        }

        var bytes = ip.GetAddressBytes();
        ushort h0 = (ushort)((bytes[0] << 8) | bytes[1]);
        ushort h1 = (ushort)((bytes[2] << 8) | bytes[3]);
        ushort h2 = (ushort)((bytes[4] << 8) | bytes[5]);
        return $"v6|{h0:x4}:{h1:x4}:{h2:x4}::/48";
    }

    private sealed record NetworkTickState(
        InterfaceSnapshot? Primary,
        VpnAssessment Vpn,
        WifiIdentityState Wifi,
        LocalNetworkIdentity Local,
        string RouteReason,
        string PublicIpFetchStatus,
        string PublicIpBucketValue,
        int PublicIpAgeSeconds);
}
