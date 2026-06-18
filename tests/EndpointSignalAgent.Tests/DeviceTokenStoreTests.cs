// tests/EndpointSignalAgent.Tests/DeviceTokenStoreTests.cs
using EndpointSignalAgent.Bootstrap.Identity;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class DeviceTokenStoreTests
{
    [Fact]
    public void Get_ReturnsNull_WhenNotSet()
    {
        var store = new DeviceTokenStore();
        Assert.Null(store.Get());
    }

    [Fact]
    public void Get_ReturnsToken_AfterSet()
    {
        var store = new DeviceTokenStore();
        store.Set("tok_abc123");
        Assert.Equal("tok_abc123", store.Get());
    }

    [Fact]
    public void Set_Overwrites_PreviousToken()
    {
        var store = new DeviceTokenStore();
        store.Set("old");
        store.Set("new");
        Assert.Equal("new", store.Get());
    }
}
