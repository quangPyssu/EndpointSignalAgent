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
}
