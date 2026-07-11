// tests/EndpointSignalAgent.Tests/DefaultDecisionHandlerTests.cs
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Handlers;
using EndpointSignalAgent.Shared.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class DefaultDecisionHandlerTests
{
    private static StatusDecision MakeDecision(string label) => new(
        DeviceId: "test-device",
        Label: label,
        Score: 0.9,
        Reason: "test",
        ModelVersion: "v1",
        WindowStartTs: 1718668800,
        DecidedAtMs: 1718668830000,
        TtlSeconds: 60);

    [Fact]
    public void Handle_Allow_UpdatesState_DoesNotCallActions()
    {
        var state = new AgentState();
        var lockCalled = false;
        var sleepCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "allow",
            state: state,
            decision: MakeDecision("allow"),
            lockWorkstation: () => lockCalled = true,
            forceSleep: () => sleepCalled = true);

        Assert.Equal("allow", state.CurrentDecision?.Label);
        Assert.False(lockCalled);
        Assert.False(sleepCalled);
    }

    [Fact]
    public void Handle_Lock_CallsLockAndUpdatesState()
    {
        var state = new AgentState();
        var lockCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "lock",
            state: state,
            decision: MakeDecision("lock"),
            lockWorkstation: () => lockCalled = true,
            forceSleep: () => { });

        Assert.True(lockCalled);
        Assert.Equal("lock", state.CurrentDecision?.Label);
    }

    [Fact]
    public void Handle_ForceSleep_CallsSleepAndUpdatesState()
    {
        var state = new AgentState();
        var sleepCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "force_sleep",
            state: state,
            decision: MakeDecision("force_sleep"),
            lockWorkstation: () => { },
            forceSleep: () => sleepCalled = true);

        Assert.True(sleepCalled);
        Assert.Equal("force_sleep", state.CurrentDecision?.Label);
    }

    [Fact]
    public void Handle_Unknown_UpdatesState_DoesNotCallActions()
    {
        var state = new AgentState();
        var lockCalled = false;
        var sleepCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "unknown",
            state: state,
            decision: MakeDecision("unknown"),
            lockWorkstation: () => lockCalled = true,
            forceSleep: () => sleepCalled = true);

        Assert.Equal("unknown", state.CurrentDecision?.Label);
        Assert.False(lockCalled);
        Assert.False(sleepCalled);
    }

    [Theory]
    [InlineData("anomaly", "scored:alert", "lock")]
    [InlineData("anomaly", "scored:watch", "unknown")]
    [InlineData("normal", "scored", "allow")]
    [InlineData("normal", "scored:watch", "allow")]
    [InlineData("unknown", "no_model", "unknown")]
    [InlineData("unknown", "cold_start", "unknown")]
    public void TranslateLabel_MapsGatewayVocabularyToDispatchLabel(
        string gatewayLabel, string reason, string expectedDispatchLabel)
    {
        var result = DefaultDecisionHandler.TranslateLabel(gatewayLabel, reason);
        Assert.Equal(expectedDispatchLabel, result);
    }

    [Fact]
    public void Handle_AlertTier_LocksOnce_ThenRespectsCooldown()
    {
        var state = new AgentState();
        var lockCount = 0;
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var handler = new DefaultDecisionHandler(
            NullLogger<DefaultDecisionHandler>.Instance,
            state,
            lockCooldownSeconds: 300,
            clock: () => now,
            lockWorkstationOverride: () => lockCount++);

        var alert = MakeDecision("anomaly") with { Reason = "scored:alert" };

        handler.Handle(alert);
        Assert.Equal(1, lockCount);

        // Second poll 30s later, still ALERT: cooldown blocks a re-lock.
        now = now.AddSeconds(30);
        handler.Handle(alert);
        Assert.Equal(1, lockCount);

        // 301s after the first lock: cooldown has elapsed, locks again.
        now = now.AddSeconds(271);
        handler.Handle(alert);
        Assert.Equal(2, lockCount);
    }

    [Fact]
    public void Handle_WatchTier_NeverLocks()
    {
        var state = new AgentState();
        var lockCount = 0;
        var handler = new DefaultDecisionHandler(
            NullLogger<DefaultDecisionHandler>.Instance,
            state,
            lockCooldownSeconds: 300,
            clock: () => DateTimeOffset.UtcNow,
            lockWorkstationOverride: () => lockCount++);

        var watch = MakeDecision("anomaly") with { Reason = "scored:watch" };
        handler.Handle(watch);

        Assert.Equal(0, lockCount);
    }
}
