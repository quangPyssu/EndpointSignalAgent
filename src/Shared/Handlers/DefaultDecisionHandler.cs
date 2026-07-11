// src/Shared/Handlers/DefaultDecisionHandler.cs
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.State;
using EndpointSignalAgent.Shared.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EndpointSignalAgent.Bootstrap.Configuration;

namespace EndpointSignalAgent.Shared.Handlers;

public sealed class DefaultDecisionHandler : IDecisionHandler
{
    private readonly ILogger<DefaultDecisionHandler> _logger;
    private readonly IAgentState _agentState;
    private readonly int _lockCooldownSeconds;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action _lockWorkstation;
    private readonly Action _forceSleep;
    private DateTimeOffset? _lastLockAt;

    public DefaultDecisionHandler(
        ILogger<DefaultDecisionHandler> logger,
        IAgentState agentState,
        IOptions<AgentOptions> options)
        : this(
            logger,
            agentState,
            options.Value.LockCooldownSeconds,
            () => DateTimeOffset.UtcNow,
            WindowsActions.LockWorkstation,
            WindowsActions.ForceSleep)
    {
    }

    // Test seam: overrides the cooldown, clock, and side-effecting actions so
    // unit tests can assert lock/no-lock behavior deterministically without a
    // real Windows session.
    internal DefaultDecisionHandler(
        ILogger<DefaultDecisionHandler> logger,
        IAgentState agentState,
        int lockCooldownSeconds,
        Func<DateTimeOffset> clock,
        Action lockWorkstationOverride,
        Action? forceSleepOverride = null)
    {
        _logger = logger;
        _agentState = agentState;
        _lockCooldownSeconds = lockCooldownSeconds;
        _clock = clock;
        _lockWorkstation = lockWorkstationOverride;
        _forceSleep = forceSleepOverride ?? (() => { });
    }

    public void Handle(StatusDecision decision)
    {
        _logger.LogInformation(
            "Decision received: label={Label} score={Score} reason={Reason}",
            decision.Label, decision.Score, decision.Reason);

        var dispatchLabel = TranslateLabel(decision.Label, decision.Reason);

        if (dispatchLabel == KnownLabels.Lock && !CooldownElapsed())
        {
            _logger.LogDebug(
                "ALERT received but lock cooldown active (last lock {LastLock}); suppressing re-lock",
                _lastLockAt);
            _agentState.SetDecision(decision);
            return;
        }

        DispatchLabel(
            label: dispatchLabel,
            state: _agentState,
            decision: decision,
            lockWorkstation: () =>
            {
                _lastLockAt = _clock();
                _lockWorkstation();
            },
            forceSleep: _forceSleep);
    }

    private bool CooldownElapsed()
    {
        if (_lastLockAt is null) return true;
        return (_clock() - _lastLockAt.Value).TotalSeconds >= _lockCooldownSeconds;
    }

    /// <summary>
    /// Maps the gateway's wire vocabulary (label in {normal, anomaly,
    /// unknown}; tier encoded as a ":watch"/":alert" suffix on reason — see
    /// backend/proto/messages.md and analyzer/scoring/escalation.py) to this
    /// client's dispatch vocabulary (KnownLabels: allow/deny/lock/force_sleep/unknown).
    /// ALERT is the only tier that locks; WATCH is a "tighten verification"
    /// state that never locks.
    /// </summary>
    internal static string TranslateLabel(string gatewayLabel, string reason)
    {
        reason ??= string.Empty;
        if (gatewayLabel == "anomaly" && reason.EndsWith(":alert", StringComparison.Ordinal))
        {
            return KnownLabels.Lock;
        }
        if (gatewayLabel == "normal")
        {
            return KnownLabels.Allow;
        }
        return KnownLabels.Unknown;
    }

    internal static void DispatchLabel(
        string label,
        IAgentState state,
        StatusDecision decision,
        Action lockWorkstation,
        Action forceSleep)
    {
        state.SetDecision(decision);

        switch (label.ToLowerInvariant())
        {
            case KnownLabels.Lock:
                lockWorkstation();
                break;
            case KnownLabels.ForceSleep:
                forceSleep();
                break;
            case KnownLabels.Allow:
            case KnownLabels.Deny:
            case KnownLabels.Unknown:
            default:
                break;
        }
    }
}
