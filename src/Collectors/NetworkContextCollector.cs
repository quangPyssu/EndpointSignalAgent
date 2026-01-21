using EndpointSignalAgent.Collectors;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

public sealed class NetworkContextCollector : SignalCollectorBase
{
    private readonly ILogger<NetworkContextCollector> _logger;

    private readonly TimeSpan _poll = TimeSpan.FromSeconds(3);

    private bool? _lastVpnOn;
    private string? _lastVpnAdapterHash;

    private bool? _lastWifiUp;
    private string? _lastLocalPrefixHash;

    public NetworkContextCollector(ILogger<NetworkContextCollector> logger, string spoolPath = @"spool\signals.jsonl")
        : base(spoolPath)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NetworkContextCollector started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = Snapshot();

                // Emit on change only (keeps noise low)
                if (_lastVpnOn != snap.VpnOn || _lastVpnAdapterHash != snap.VpnAdapterHash)
                {
                    _lastVpnOn = snap.VpnOn;
                    _lastVpnAdapterHash = snap.VpnAdapterHash;

                    await WriteSignalAsync(SignalEventType.VpnStateChanged, new Dictionary<string, string>
                    {
                        ["vpnOn"] = snap.VpnOn ? "true" : "false",
                        ["vpnAdapter"] = snap.VpnAdapterHash ?? "none"
                    });
                }

                if (_lastWifiUp != snap.WifiUp)
                {
                    _lastWifiUp = snap.WifiUp;

                    await WriteSignalAsync(SignalEventType.WifiLinkChanged, new Dictionary<string, string>
                    {
                        ["wifiUp"] = snap.WifiUp ? "true" : "false"
                    });
                }

                if (_lastLocalPrefixHash != snap.LocalPrefixHash)
                {
                    _lastLocalPrefixHash = snap.LocalPrefixHash;

                    await WriteSignalAsync(SignalEventType.LocalNetworkChanged, new Dictionary<string, string>
                    {
                        ["localPrefix"] = snap.LocalPrefixHash ?? "none"
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NetworkContextCollector loop error.");
            }

            await Task.Delay(_poll, stoppingToken);
        }

        _logger.LogInformation("NetworkContextCollector stopped.");
    }

    private static (bool VpnOn, string? VpnAdapterHash, bool WifiUp, string? LocalPrefixHash) Snapshot()
    {
        bool vpnOn = false;
        string? vpnAdapter = null;

        bool wifiUp = false;
        string? localPrefix = null;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            var type = ni.NetworkInterfaceType;
            var desc = (ni.Description ?? "").ToLowerInvariant();
            var name = (ni.Name ?? "").ToLowerInvariant();

            // Heuristic VPN detection (good enough to start):
            // - Tunnel is the big one; plus common keywords.
            bool isVpn =
                type == NetworkInterfaceType.Tunnel ||
                desc.Contains("vpn") || desc.Contains("wireguard") || desc.Contains("openvpn") ||
                desc.Contains("tunnel") || desc.Contains("tap") || desc.Contains("tunsafe") ||
                name.Contains("vpn") || name.Contains("wireguard") || name.Contains("openvpn");

            if (isVpn)
            {
                vpnOn = true;
                vpnAdapter ??= HashStable($"{ni.Id}|{ni.Description}|{ni.Name}");
            }

            if (type == NetworkInterfaceType.Wireless80211)
                wifiUp = true;

            // A coarse local network fingerprint: first IPv4 unicast address /24 bucket, hashed.
            // (Optional; you can remove if you want to be extra conservative.)
            if (localPrefix is null)
            {
                var ipProps = ni.GetIPProperties();
                var v4 = ipProps.UnicastAddresses
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (v4 is not null)
                {
                    var bytes = v4.GetAddressBytes();
                    var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
                    localPrefix = HashStable(prefix);
                }
            }
        }

        return (vpnOn, vpnAdapter, wifiUp, localPrefix);
    }

    private static string HashStable(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 12)); // 24 hex chars
    }
}
