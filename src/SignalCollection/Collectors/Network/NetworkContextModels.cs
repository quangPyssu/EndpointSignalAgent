using System.Net;
using System.Net.NetworkInformation;

namespace EndpointSignalAgent.SignalCollection.Collectors.Network;

internal sealed record InterfaceSnapshot(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    OperationalStatus Status,
    int? IPv4Index,
    int? IPv6Index,
    Guid? AdapterGuid,
    IReadOnlyList<IPAddress> UnicastAddresses,
    IReadOnlyList<IPAddress> GatewayAddresses,
    IReadOnlyList<IPAddress> DnsAddresses);

internal sealed record PrimaryInterfaceResult(InterfaceSnapshot? Interface, string ReasonCode);

internal sealed record RouteEntry(
    IPAddress PrefixAddress,
    int PrefixLength,
    int InterfaceIndex,
    NetworkInterfaceType InterfaceType,
    bool IsTunnelLike);

internal sealed record WlanIdentity(
    bool ApiAvailable,
    bool Connected,
    Guid InterfaceGuid,
    string? Ssid,
    string? Bssid,
    string ReasonCode);

internal sealed record RasState(
    bool ApiAvailable,
    bool HasActiveConnection,
    IReadOnlyList<string> ConnectionDeviceNames,
    string ReasonCode);

internal sealed record VpnAssessment(bool VpnOn, string Confidence, string ReasonCode, string? AdapterFingerprint);

internal sealed record LocalNetworkIdentity(
    string LocalPrefixValue,
    string LocalNetworkValue,
    string IpFamily,
    string ReasonCode);

internal sealed record PublicIpSample(
    string Status,
    string? BucketHash,
    DateTimeOffset? LastSuccessUtc);

internal sealed record WifiIdentityState(
    bool WifiUp,
    string WifiSsidValue,
    string WifiBssidValue,
    string Confidence,
    string ReasonCode);
