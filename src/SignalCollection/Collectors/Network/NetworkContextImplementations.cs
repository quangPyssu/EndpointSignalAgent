using Microsoft.Win32;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EndpointSignalAgent.SignalCollection.Collectors.Network;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class NetworkInterfaceSnapshotProvider : INetworkInterfaceSnapshotProvider
{
    public IReadOnlyList<InterfaceSnapshot> GetInterfaces()
    {
        var result = new List<InterfaceSnapshot>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties? props = null;
            int? ipv4Index = null;
            int? ipv6Index = null;
            Guid? adapterGuid = null;
            var unicast = new List<IPAddress>();
            var gateways = new List<IPAddress>();
            var dns = new List<IPAddress>();

            try
            {
                props = ni.GetIPProperties();
                ipv4Index = props.GetIPv4Properties()?.Index;
                ipv6Index = props.GetIPv6Properties()?.Index;
                unicast.AddRange(props.UnicastAddresses.Select(x => x.Address));
                gateways.AddRange(props.GatewayAddresses.Select(x => x.Address));
                dns.AddRange(props.DnsAddresses);
            }
            catch
            {
                // Keep partial snapshot; collector will downgrade confidence/reason.
            }

            if (Guid.TryParse(ni.Id, out var parsedGuid))
            {
                adapterGuid = parsedGuid;
            }

            result.Add(new InterfaceSnapshot(
                ni.Id,
                ni.Name ?? string.Empty,
                ni.Description ?? string.Empty,
                ni.NetworkInterfaceType,
                ni.OperationalStatus,
                ipv4Index,
                ipv6Index,
                adapterGuid,
                unicast,
                gateways,
                dns));
        }

        return result;
    }
}

internal sealed class PrimaryInterfaceResolver : IPrimaryInterfaceResolver
{
    private readonly IPrimaryInterfaceIndexProvider _indexProvider;

    public PrimaryInterfaceResolver(IPrimaryInterfaceIndexProvider indexProvider)
    {
        _indexProvider = indexProvider;
    }

    public PrimaryInterfaceResult Resolve(IReadOnlyList<InterfaceSnapshot> interfaces)
    {
        if (_indexProvider.TryGetPrimaryInterfaceIndex(out var index, out var reason))
        {
            var byIndex = interfaces.FirstOrDefault(i => i.IPv4Index == index || i.IPv6Index == index);
            if (byIndex is not null)
            {
                return new PrimaryInterfaceResult(byIndex, "default_route");
            }

            return new PrimaryInterfaceResult(null, "default_route_unmapped");
        }

        var fallback = interfaces
            .Where(i => i.Status == OperationalStatus.Up)
            .OrderBy(i => i.InterfaceType == NetworkInterfaceType.Loopback ? 1 : 0)
            .ThenBy(i => i.InterfaceType == NetworkInterfaceType.Tunnel ? 1 : 0)
            .FirstOrDefault();

        if (fallback is not null)
        {
            return new PrimaryInterfaceResult(fallback, $"fallback_{reason}");
        }

        return new PrimaryInterfaceResult(null, $"none_{reason}");
    }
}

internal sealed class WindowsPrimaryInterfaceIndexProvider : IPrimaryInterfaceIndexProvider
{
    public bool TryGetPrimaryInterfaceIndex(out int index, out string reasonCode)
    {
        index = 0;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            reasonCode = "non_windows";
            return false;
        }

        var destination = new byte[] { 1, 1, 1, 1 };
        uint destinationNetworkOrder = BinaryPrimitives.ReadUInt32BigEndian(destination);
        var result = GetBestInterface(destinationNetworkOrder, out var ifIndex);
        if (result != 0)
        {
            reasonCode = $"api_fail_{result}";
            return false;
        }

        index = unchecked((int)ifIndex);
        reasonCode = "ok";
        return true;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
}

internal sealed class WindowsRouteTableReader : IRouteTableReader
{
    public bool TryReadRoutes(IReadOnlyList<InterfaceSnapshot> interfaces, out IReadOnlyList<RouteEntry> routes, out string reasonCode)
    {
        routes = Array.Empty<RouteEntry>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            reasonCode = "non_windows";
            return false;
        }

        IntPtr tablePtr = IntPtr.Zero;
        try
        {
            var err = GetIpForwardTable2(AF_UNSPEC, out tablePtr);
            if (err != 0 || tablePtr == IntPtr.Zero)
            {
                reasonCode = $"api_fail_{err}";
                return false;
            }

            var header = Marshal.PtrToStructure<MIB_IPFORWARD_TABLE2>(tablePtr);
            var rowSize = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            var basePtr = IntPtr.Add(tablePtr, Marshal.SizeOf<MIB_IPFORWARD_TABLE2>());
            var list = new List<RouteEntry>((int)header.NumEntries);

            for (int i = 0; i < header.NumEntries; i++)
            {
                var rowPtr = IntPtr.Add(basePtr, i * rowSize);
                var row = Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(rowPtr);
                if (!TryConvertSockaddr(row.DestinationPrefix.Prefix, out var prefix))
                {
                    continue;
                }

                var iface = interfaces.FirstOrDefault(x => x.IPv4Index == unchecked((int)row.InterfaceIndex) || x.IPv6Index == unchecked((int)row.InterfaceIndex));
                var ifaceType = iface?.InterfaceType ?? NetworkInterfaceType.Unknown;
                var tunnelLike = ifaceType == NetworkInterfaceType.Tunnel || ifaceType == NetworkInterfaceType.Ppp;

                list.Add(new RouteEntry(
                    prefix,
                    row.DestinationPrefix.PrefixLength,
                    unchecked((int)row.InterfaceIndex),
                    ifaceType,
                    tunnelLike));
            }

            routes = list;
            reasonCode = "ok";
            return true;
        }
        catch
        {
            reasonCode = "marshal_fail";
            return false;
        }
        finally
        {
            if (tablePtr != IntPtr.Zero)
            {
                FreeMibTable(tablePtr);
            }
        }
    }

    private static bool TryConvertSockaddr(SOCKADDR_INET addr, out IPAddress ip)
    {
        if (addr.si_family == AF_INET)
        {
            var bytes = BitConverter.GetBytes(addr.Ipv4.sin_addr);
            ip = new IPAddress(bytes);
            return true;
        }

        if (addr.si_family == AF_INET6)
        {
            ip = new IPAddress(addr.Ipv6.sin6_addr, addr.Ipv6.sin6_scope_id);
            return true;
        }

        ip = IPAddress.None;
        return false;
    }

    private const ushort AF_UNSPEC = 0;
    private const ushort AF_INET = 2;
    private const ushort AF_INET6 = 23;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARD_TABLE2
    {
        public uint NumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IP_ADDRESS_PREFIX DestinationPrefix;
        public SOCKADDR_INET NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SOCKADDR_INET
    {
        [FieldOffset(0)]
        public SOCKADDR_IN Ipv4;
        [FieldOffset(0)]
        public SOCKADDR_IN6 Ipv6;
        [FieldOffset(0)]
        public short si_family;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKADDR_IN
    {
        public short sin_family;
        public ushort sin_port;
        public uint sin_addr;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] sin_zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKADDR_IN6
    {
        public short sin6_family;
        public ushort sin6_port;
        public uint sin6_flowinfo;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] sin6_addr;

        public uint sin6_scope_id;
    }
}

internal sealed class WindowsRasReader : IRasReader
{
    public RasState Read()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new RasState(false, false, Array.Empty<string>(), "non_windows");
        }

        try
        {
            var size = (uint)Marshal.SizeOf<RASCONN>();
            var bufferSize = size;
            var first = new RASCONN { dwSize = size };
            var firstArray = new[] { first };

            var result = RasEnumConnections(firstArray, ref bufferSize, out var connectionCount);
            if (result == ERROR_BUFFER_TOO_SMALL)
            {
                var neededCount = (int)(bufferSize / size);
                var buffer = new RASCONN[Math.Max(neededCount, 1)];
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i].dwSize = size;
                }

                result = RasEnumConnections(buffer, ref bufferSize, out connectionCount);
                if (result != 0)
                {
                    return new RasState(true, false, Array.Empty<string>(), $"api_fail_{result}");
                }

                var devices = buffer
                    .Take((int)connectionCount)
                    .Select(x => (x.szDeviceName ?? string.Empty).Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new RasState(true, connectionCount > 0, devices, connectionCount > 0 ? "active" : "none");
            }

            if (result != 0)
            {
                return new RasState(true, false, Array.Empty<string>(), $"api_fail_{result}");
            }

            var baseDevices = firstArray
                .Take((int)connectionCount)
                .Select(x => (x.szDeviceName ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new RasState(true, connectionCount > 0, baseDevices, connectionCount > 0 ? "active" : "none");
        }
        catch
        {
            return new RasState(true, false, Array.Empty<string>(), "marshal_fail");
        }
    }

    private const uint ERROR_BUFFER_TOO_SMALL = 603;

    [DllImport("rasapi32.dll", CharSet = CharSet.Auto)]
    private static extern uint RasEnumConnections(
        [In, Out] RASCONN[] lprasconn,
        ref uint lpcb,
        out uint lpcConnections);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct RASCONN
    {
        public uint dwSize;
        public IntPtr hrasconn;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szEntryName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string szDeviceType;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szDeviceName;
    }
}

internal sealed class WindowsWlanReader : IWlanReader
{
    public WlanIdentity ReadByInterface(Guid interfaceGuid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WlanIdentity(false, false, interfaceGuid, null, null, "non_windows");
        }

        IntPtr client = IntPtr.Zero;
        IntPtr ifListPtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;
        try
        {
            var err = WlanOpenHandle(2, IntPtr.Zero, out _, out client);
            if (err != 0)
            {
                return new WlanIdentity(true, false, interfaceGuid, null, null, $"api_fail_open_{err}");
            }

            err = WlanEnumInterfaces(client, IntPtr.Zero, out ifListPtr);
            if (err != 0 || ifListPtr == IntPtr.Zero)
            {
                return new WlanIdentity(true, false, interfaceGuid, null, null, $"api_fail_enum_{err}");
            }

            var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(ifListPtr);
            long current = ifListPtr.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>();
            int infoSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

            for (int i = 0; i < header.dwNumberOfItems; i++)
            {
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(new IntPtr(current));
                current += infoSize;

                if (info.InterfaceGuid != interfaceGuid)
                {
                    continue;
                }

                err = WlanQueryInterface(
                    client,
                    ref info.InterfaceGuid,
                    WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                    IntPtr.Zero,
                    out _,
                    out dataPtr,
                    out _);

                if (err != 0 || dataPtr == IntPtr.Zero)
                {
                    return new WlanIdentity(true, false, interfaceGuid, null, null, $"api_fail_query_{err}");
                }

                var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                if (conn.isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                {
                    return new WlanIdentity(true, false, interfaceGuid, null, null, "disconnected");
                }

                var ssidLength = Math.Min((int)conn.wlanAssociationAttributes.dot11Ssid.SSIDLength, conn.wlanAssociationAttributes.dot11Ssid.SSID.Length);
                var ssid = ssidLength > 0
                    ? Encoding.UTF8.GetString(conn.wlanAssociationAttributes.dot11Ssid.SSID, 0, ssidLength)
                    : null;

                var bssidBytes = conn.wlanAssociationAttributes.dot11Bssid;
                string? bssid = null;
                if (bssidBytes is { Length: 6 } && bssidBytes.Any(x => x != 0))
                {
                    bssid = Convert.ToHexString(bssidBytes);
                }

                return new WlanIdentity(true, true, interfaceGuid, ssid, bssid, "connected");
            }

            return new WlanIdentity(true, false, interfaceGuid, null, null, "interface_not_found");
        }
        catch
        {
            return new WlanIdentity(true, false, interfaceGuid, null, null, "marshal_fail");
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
            {
                try { WlanFreeMemory(dataPtr); } catch { }
            }

            if (ifListPtr != IntPtr.Zero)
            {
                try { WlanFreeMemory(ifListPtr); } catch { }
            }

            if (client != IntPtr.Zero)
            {
                try { WlanCloseHandle(client, IntPtr.Zero); } catch { }
            }
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        WLAN_INTF_OPCODE opCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        out WLAN_OPCODE_VALUE_TYPE pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    private enum WLAN_INTF_OPCODE
    {
        wlan_intf_opcode_current_connection = 7
    }

    private enum WLAN_OPCODE_VALUE_TYPE
    {
        wlan_opcode_value_type_query_only = 0
    }

    private enum WLAN_INTERFACE_STATE
    {
        wlan_interface_state_connected = 1,
        wlan_interface_state_disconnected = 4
    }

    private enum WLAN_CONNECTION_MODE
    {
        wlan_connection_mode_profile = 0
    }

    private enum DOT11_BSS_TYPE
    {
        dot11_BSS_type_infrastructure = 1
    }

    private enum DOT11_PHY_TYPE
    {
        dot11_phy_type_unknown = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public int dwNumberOfItems;
        public int dwIndex;
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

internal sealed class StableSaltProvider : ISaltProvider
{
    private readonly object _lock = new();
    private string? _cachedSalt;

    public string GetStableSalt()
    {
        if (_cachedSalt is not null)
        {
            return _cachedSalt;
        }

        lock (_lock)
        {
            if (_cachedSalt is not null)
            {
                return _cachedSalt;
            }

            var machineGuid = TryGetMachineGuid();
            if (!string.IsNullOrWhiteSpace(machineGuid))
            {
                _cachedSalt = machineGuid;
                return _cachedSalt;
            }

            var persisted = TryLoadOrCreateSecret();
            _cachedSalt = persisted ?? $"fallback|{Environment.MachineName}|{Environment.UserName}";
            return _cachedSalt;
        }
    }

    private static string? TryGetMachineGuid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryLoadOrCreateSecret()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EndpointSignalAgent", "secret.bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndpointSignalAgent", "secret.bin")
        };

        foreach (var path in paths)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length >= 16)
                    {
                        return Convert.ToHexString(bytes);
                    }
                }

                var generated = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(path, generated);
                return Convert.ToHexString(generated);
            }
            catch
            {
                // Try next location.
            }
        }

        return null;
    }
}

internal sealed class HashingService : IHashingService
{
    private readonly ISaltProvider _saltProvider;

    public HashingService(ISaltProvider saltProvider)
    {
        _saltProvider = saltProvider;
    }

    public string HashStable(string value)
    {
        var salted = $"{_saltProvider.GetStableSalt()}|{value}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
        return Convert.ToHexString(bytes.AsSpan(0, 12));
    }
}

internal sealed class HttpPublicIpProvider : IPublicIpProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _url;

    public HttpPublicIpProvider(string name, string url, HttpClient httpClient)
    {
        Name = name;
        _url = url;
        _httpClient = httpClient;
    }

    public string Name { get; }

    public async Task<IPAddress?> TryGetPublicIpAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.UserAgent.ParseAdd("EndpointSignalAgent/1.0");
        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = (await resp.Content.ReadAsStringAsync(cancellationToken)).Trim();
        return IPAddress.TryParse(body, out var ip) ? ip : null;
    }
}
