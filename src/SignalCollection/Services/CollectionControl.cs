namespace EndpointSignalAgent.SignalCollection.Services;

public interface ICollectionControl
{
    bool IsPaused { get; }
    void Pause();
    void Resume();
    bool IsSessionLocked { get; }
    void SetSessionLocked(bool locked);
}

public sealed class CollectionControl : ICollectionControl
{
    private int _paused;
    private int _sessionLocked;

    public bool IsPaused => Volatile.Read(ref _paused) == 1;
    public void Pause() => Interlocked.Exchange(ref _paused, 1);
    public void Resume() => Interlocked.Exchange(ref _paused, 0);

    public bool IsSessionLocked => Volatile.Read(ref _sessionLocked) == 1;
    public void SetSessionLocked(bool locked) => Interlocked.Exchange(ref _sessionLocked, locked ? 1 : 0);
}
