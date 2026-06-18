// src/Shared/State/AgentState.cs
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.Shared.State;

public interface IAgentState
{
    int GetReportSecondsOrDefault(int fallbackSeconds);
    void TrySetReportSeconds(int seconds);
    StatusDecision? CurrentDecision { get; }
    void SetDecision(StatusDecision decision);
}

public sealed class AgentState : IAgentState
{
    private int _reportSeconds;
    private volatile StatusDecision? _currentDecision;

    public StatusDecision? CurrentDecision => _currentDecision;

    public int GetReportSecondsOrDefault(int fallbackSeconds)
    {
        var v = Volatile.Read(ref _reportSeconds);
        return v > 0 ? v : fallbackSeconds;
    }

    public void TrySetReportSeconds(int seconds)
    {
        if (seconds is >= 1 and <= 3600)
            Interlocked.Exchange(ref _reportSeconds, seconds);
    }

    public void SetDecision(StatusDecision decision) => _currentDecision = decision;
}
