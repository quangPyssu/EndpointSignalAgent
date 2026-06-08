using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Shared.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class DeviceGuardServiceTests
{
    private static AgentOptions.DeviceGuardOptions Opts(
        bool enabled = true,
        int lockSec = 600,
        int sleepSec = 0) =>
        new AgentOptions.DeviceGuardOptions
        {
            Enabled = enabled,
            IdleLockThresholdSec = lockSec,
            IdleSleepThresholdSec = sleepSec,
            PollIntervalSec = 1
        };

    [Fact]
    public void EvaluateIdle_DoesNothing_WhenBelowAllThresholds()
    {
        var lockCalled = false;
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 100,
            opts: Opts(lockSec: 600, sleepSec: 0),
            lockWorkstation: () => lockCalled = true,
            sleep: () => sleepCalled = true);

        Assert.False(lockCalled);
        Assert.False(sleepCalled);
    }

    [Fact]
    public void EvaluateIdle_CallsLock_WhenIdleExceedsLockThreshold()
    {
        var lockCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 700,
            opts: Opts(lockSec: 600, sleepSec: 0),
            lockWorkstation: () => lockCalled = true,
            sleep: () => { });

        Assert.True(lockCalled);
    }

    [Fact]
    public void EvaluateIdle_CallsSleep_WhenIdleExceedsSleepThreshold()
    {
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 1300,
            opts: Opts(lockSec: 600, sleepSec: 1200),
            lockWorkstation: () => { },
            sleep: () => sleepCalled = true);

        Assert.True(sleepCalled);
    }

    [Fact]
    public void EvaluateIdle_DoesNotCallSleep_WhenSleepThresholdIsZero()
    {
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 9999,
            opts: Opts(lockSec: 600, sleepSec: 0),
            lockWorkstation: () => { },
            sleep: () => sleepCalled = true);

        Assert.False(sleepCalled);
    }

    [Fact]
    public void EvaluateIdle_PrefersLock_WhenOnlyLockThresholdExceeded()
    {
        var lockCalled = false;
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 700,
            opts: Opts(lockSec: 600, sleepSec: 1200),
            lockWorkstation: () => lockCalled = true,
            sleep: () => sleepCalled = true);

        Assert.True(lockCalled);
        Assert.False(sleepCalled);
    }
}
