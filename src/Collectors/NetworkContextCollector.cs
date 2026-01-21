using EndpointSignalAgent.Collectors;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Network/VPN context collector.
/// 
/// Proposal-aligned outputs (privacy-preserving):
/// - VPN on/off (+ hashed adapter fingerprint)
/// - Wi-Fi SSID hashed
/// - Public IP "bucket" hashed (coarse, e.g., /24 for IPv4, /48 for IPv6)
/// 
/// Note: This collector intentionally avoids logging raw SSIDs or full IP addresses.
/// </summary>
public sealed class NetworkContextCollector : SignalCollectorBase
{
    private readonly ILogger<NetworkContextCollector> _logger;

    private readonly TimeSpan _poll = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _publicIpPoll = TimeSpan.FromSeconds(60);
    private DateTimeOffset _nextPublicIpPoll = DateTimeOffset.MinValue;

    private bool? _lastVpnOn;
    private string? _lastVpnAdapterHash;

    private bool? _lastWifiUp;
    // Sentinel so we emit one baseline event on startup (even if SSID is "none").
    private string? _lastWifiSsidHash = "__init__";

    private string? _lastLocalPrefixHash;
    // Sentinel so the first successful fetch emits a baseline event.
    private string? _lastPublicIpBucketHash = "__init__";

    // One shared client; keep it tiny + timeout short.
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly string? _machineSalt = TryGetMachineGuid();

    public NetworkContextCollector(ILogger<NetworkContextCollector> logger, string spoolPath = @"spool\signals.jsonl")
        : base(spoolPath)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");
        _logger.LogInformation("NetworkContextCollector started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var snap = SnapshotLocal();

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

                // Proposal-aligned: hashed SSID (only when available)
                var ssidHash = TryGetWifiSsidHash();
                if (_lastWifiSsidHash != ssidHash)
                {
                    _lastWifiSsidHash = ssidHash;

                    await WriteSignalAsync(SignalEventType.WifiSsidChanged, new Dictionary<string, string>
                    {
                        ["wifiSsid"] = ssidHash ?? "none",
                        ["wifiUp"] = snap.WifiUp ? "true" : "false"
                    });
                }

                // Optional local fingerprint (still privacy-preserving); keep if you find it useful.
                if (_lastLocalPrefixHash != snap.LocalPrefixHash)
                {
                    _lastLocalPrefixHash = snap.LocalPrefixHash;

                    await WriteSignalAsync(SignalEventType.LocalNetworkChanged, new Dictionary<string, string>
                    {
                        ["localPrefix"] = snap.LocalPrefixHash ?? "none"
                    });
                }

                // Proposal-aligned: coarse public IP bucket (poll slower to avoid noise)
                if (now >= _nextPublicIpPoll)
                {
                    _nextPublicIpPoll = now + _publicIpPoll;

                    var publicBucketHash = await TryGetPublicIpBucketHashAsync(stoppingToken);
                    if (publicBucketHash is not null && publicBucketHash != _lastPublicIpBucketHash)
                    {
                        _lastPublicIpBucketHash = publicBucketHash;

                        await WriteSignalAsync(SignalEventType.PublicIpBucketChanged, new Dictionary<string, string>
                        {
                            ["publicIpBucket"] = publicBucketHash
                        });
                    }
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

    private static (bool VpnOn, string? VpnAdapterHash, bool WifiUp, string? LocalPrefixHash) SnapshotLocal()
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
                vpnAdapter ??= HashStable($"vpn|{ni.Id}|{ni.Description}|{ni.Name}");
            }

            if (type == NetworkInterfaceType.Wireless80211)
                wifiUp = true;

            // Coarse local network fingerprint: first IPv4 unicast address /24 bucket, hashed.
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
                    localPrefix = HashStable($"local4|{prefix}");
                }
            }
        }

        return (vpnOn, vpnAdapter, wifiUp, localPrefix);
    }

    private static string? TryGetWifiSsidHash()
    {
        var ssid = TryGetWifiSsid();
        if (string.IsNullOrWhiteSpace(ssid)) return null;
        return HashStable($"ssid|{ssid.Trim()}");
    }

    private static async Task<string?> TryGetPublicIpBucketHashAsync(CancellationToken ct)
    {
        try
        {
            // Plain text response (e.g. "203.0.113.1" or IPv6)
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org");
            req.Headers.UserAgent.ParseAdd("EndpointSignalAgent/1.0");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var ipText = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            if (string.IsNullOrWhiteSpace(ipText)) return null;

            if (!IPAddress.TryParse(ipText, out var ip)) return null;

            return HashStable($"pub|{ComputeIpBucketString(ip)}");
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeIpBucketString(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return $"v4|{b[0]}.{b[1]}.{b[2]}.0/24";
        }

        // IPv6: /48 bucket => first 3 hextets
        var bytes = ip.GetAddressBytes();
        ushort h0 = (ushort)((bytes[0] << 8) | bytes[1]);
        ushort h1 = (ushort)((bytes[2] << 8) | bytes[3]);
        ushort h2 = (ushort)((bytes[4] << 8) | bytes[5]);
        return $"v6|{h0:x4}:{h1:x4}:{h2:x4}::/48";
    }

    private static string HashStable(string input)
    {
        // Optional per-machine salt to make hashes non-linkable across devices.
        // If unavailable, hash remains stable but more dictionary-guessable.
        var salted = _machineSalt is null ? input : $"{_machineSalt}|{input}";

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
        return Convert.ToHexString(bytes.AsSpan(0, 12)); // 24 hex chars
    }

    private static string? TryGetMachineGuid()
    {
        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
        }
        catch
        {
            return null;
        }
    }

    // ---- Native Wi-Fi (SSID) via wlanapi.dll ----

    private static string? TryGetWifiSsid()
    {
        IntPtr client = IntPtr.Zero;
        IntPtr ifListPtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;

        try
        {
            var err = WlanOpenHandle(2, IntPtr.Zero, out _, out client);
            if (err != 0) return null;

            err = WlanEnumInterfaces(client, IntPtr.Zero, out ifListPtr);
            if (err != 0 || ifListPtr == IntPtr.Zero) return null;

            // Read header
            var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(ifListPtr);
            long current = ifListPtr.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>();
            int infoSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

            for (int i = 0; i < header.dwNumberOfItems; i++)
            {
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(new IntPtr(current));
                current += infoSize;

                // Query current connection
                err = WlanQueryInterface(
                    client,
                    ref info.InterfaceGuid,
                    WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                    IntPtr.Zero,
                    out uint dataSize,
                    out dataPtr,
                    out _);

                if (err != 0 || dataPtr == IntPtr.Zero) continue;

                try
                {
                    var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                    if (conn.isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                        continue;

                    var ssidBytes = conn.wlanAssociationAttributes.dot11Ssid.SSID;
                    var len = (int)conn.wlanAssociationAttributes.dot11Ssid.SSIDLength;
                    if (len <= 0 || len > ssidBytes.Length) continue;

                    // SSID is bytes; usually ASCII/UTF-8 compatible
                    return Encoding.UTF8.GetString(ssidBytes, 0, len);
                }
                finally
                {
                    try { if (dataPtr != IntPtr.Zero) WlanFreeMemory(dataPtr); } catch { }
                    dataPtr = IntPtr.Zero;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (dataPtr != IntPtr.Zero) WlanFreeMemory(dataPtr); } catch { }
            try { if (ifListPtr != IntPtr.Zero) WlanFreeMemory(ifListPtr); } catch { }
            try { if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); } catch { }
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint dwClientVersion,
        IntPtr pReserved,
        out uint pdwNegotiatedVersion,
        out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        WLAN_INTF_OPCODE OpCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        out WLAN_OPCODE_VALUE_TYPE pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    private enum WLAN_INTF_OPCODE
    {
        wlan_intf_opcode_autoconf_enabled = 1,
        wlan_intf_opcode_background_scan_enabled,
        wlan_intf_opcode_media_streaming_mode,
        wlan_intf_opcode_radio_state,
        wlan_intf_opcode_bss_type,
        wlan_intf_opcode_interface_state,
        wlan_intf_opcode_current_connection = 7,
        wlan_intf_opcode_channel_number,
        wlan_intf_opcode_supported_infrastructure_auth_cipher_pairs,
        wlan_intf_opcode_supported_adhoc_auth_cipher_pairs,
        wlan_intf_opcode_supported_country_or_region_string_list,
        wlan_intf_opcode_current_operation_mode,
        wlan_intf_opcode_supported_safe_mode
    }

    private enum WLAN_OPCODE_VALUE_TYPE
    {
        wlan_opcode_value_type_query_only = 0,
        wlan_opcode_value_type_set_by_group_policy,
        wlan_opcode_value_type_set_by_user,
        wlan_opcode_value_type_invalid
    }

    private enum WLAN_INTERFACE_STATE
    {
        wlan_interface_state_not_ready = 0,
        wlan_interface_state_connected = 1,
        wlan_interface_state_ad_hoc_network_formed = 2,
        wlan_interface_state_disconnecting = 3,
        wlan_interface_state_disconnected = 4,
        wlan_interface_state_associating = 5,
        wlan_interface_state_discovering = 6,
        wlan_interface_state_authenticating = 7
    }

    private enum WLAN_CONNECTION_MODE
    {
        wlan_connection_mode_profile = 0,
        wlan_connection_mode_temporary_profile,
        wlan_connection_mode_discovery_secure,
        wlan_connection_mode_discovery_unsecure,
        wlan_connection_mode_auto,
        wlan_connection_mode_invalid
    }

    private enum DOT11_BSS_TYPE
    {
        dot11_BSS_type_infrastructure = 1,
        dot11_BSS_type_independent = 2,
        dot11_BSS_type_any = 3
    }

    private enum DOT11_PHY_TYPE
    {
        dot11_phy_type_unknown = 0,
        dot11_phy_type_any = 0,
        dot11_phy_type_fhss = 1,
        dot11_phy_type_dsss = 2,
        dot11_phy_type_irbaseband = 3,
        dot11_phy_type_ofdm = 4,
        dot11_phy_type_hrdsss = 5,
        dot11_phy_type_erp = 6,
        dot11_phy_type_ht = 7,
        dot11_phy_type_vht = 8,
        dot11_phy_type_dmg = 9,
        dot11_phy_type_he = 10
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public int dwNumberOfItems;
        public int dwIndex;
        // Followed by WLAN_INTERFACE_INFO[dwNumberOfItems]
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;

        public WLAN_INTERFACE_STATE isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint SSIDLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public DOT11_BSS_TYPE dot11BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;

        public DOT11_PHY_TYPE dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bOneXEnabled;

        // We don't need the rest for SSID.
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public WLAN_INTERFACE_STATE isState;
        public WLAN_CONNECTION_MODE wlanConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;

        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }
}
