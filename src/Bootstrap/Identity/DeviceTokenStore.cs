// src/Bootstrap/Identity/DeviceTokenStore.cs
namespace EndpointSignalAgent.Bootstrap.Identity;

public sealed class DeviceTokenStore
{
    private volatile string? _token;

    public string? Get() => _token;

    public void Set(string token) => _token = token;
}
