using System.Net;

namespace EndpointSignalAgent.SignalCollection.Collectors.Network;

internal interface INetworkInterfaceSnapshotProvider
{
    IReadOnlyList<InterfaceSnapshot> GetInterfaces();
}

internal interface IPrimaryInterfaceIndexProvider
{
    bool TryGetPrimaryInterfaceIndex(out int index, out string reasonCode);
}

internal interface IPrimaryInterfaceResolver
{
    PrimaryInterfaceResult Resolve(IReadOnlyList<InterfaceSnapshot> interfaces);
}

internal interface IRouteTableReader
{
    bool TryReadRoutes(IReadOnlyList<InterfaceSnapshot> interfaces, out IReadOnlyList<RouteEntry> routes, out string reasonCode);
}

internal interface IRasReader
{
    RasState Read();
}

internal interface IWlanReader
{
    WlanIdentity ReadByInterface(Guid interfaceGuid);
}

internal interface ISaltProvider
{
    string GetStableSalt();
}

internal interface IHashingService
{
    string HashStable(string value);
}

internal interface IPublicIpProvider
{
    string Name { get; }
    Task<IPAddress?> TryGetPublicIpAsync(CancellationToken cancellationToken);
}

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
}
