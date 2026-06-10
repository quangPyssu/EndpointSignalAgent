using EndpointSignalAgent.SignalCollection.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class CollectionControlLockTests
{
    [Fact]
    public void IsSessionLocked_DefaultsFalse()
    {
        var control = new CollectionControl();
        Assert.False(control.IsSessionLocked);
    }

    [Fact]
    public void SetSessionLocked_True_ReflectsInProperty()
    {
        var control = new CollectionControl();
        control.SetSessionLocked(true);
        Assert.True(control.IsSessionLocked);
    }

    [Fact]
    public void SetSessionLocked_False_ClearsLock()
    {
        var control = new CollectionControl();
        control.SetSessionLocked(true);
        control.SetSessionLocked(false);
        Assert.False(control.IsSessionLocked);
    }

    [Fact]
    public void IsSessionLocked_DoesNotAffectIsPaused()
    {
        var control = new CollectionControl();
        control.SetSessionLocked(true);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public void IsPaused_DoesNotAffectIsSessionLocked()
    {
        var control = new CollectionControl();
        control.Pause();
        Assert.False(control.IsSessionLocked);
    }
}
