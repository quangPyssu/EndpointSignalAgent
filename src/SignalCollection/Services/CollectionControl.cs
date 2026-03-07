namespace EndpointSignalAgent.SignalCollection.Services;

public interface ICollectionControl
{
    bool IsPaused { get; }
    void Pause();
    void Resume();
}

public sealed class CollectionControl : ICollectionControl
{
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    public void Pause() => Interlocked.Exchange(ref _paused, 1);

    public void Resume() => Interlocked.Exchange(ref _paused, 0);
}
