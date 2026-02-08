namespace EndpointSignalAgent.Shared.State;

public interface IAgentState
{
    int GetReportSecondsOrDefault(int fallbackSeconds);
    void TrySetReportSeconds(int seconds);
}

public sealed class AgentState : IAgentState
{
    private int _reportSeconds = 0;

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
}
