using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class FeatureStoreCapTests : IDisposable
{
    private readonly string _dbPath;
    private readonly FeatureStore _store;

    public FeatureStoreCapTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cap_test_{Guid.NewGuid():N}.db");
        _store = new FeatureStore(NullLogger<FeatureStore>.Instance, _dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task CountUnsentAsync_ReturnsCorrectCount()
    {
        await StoreRows(3);
        Assert.Equal(3, await _store.CountUnsentAsync());
    }

    [Fact]
    public async Task PruneUnsentToCapAsync_DeletesOldestWhenOverCap()
    {
        await StoreRows(10);
        await _store.PruneUnsentToCapAsync(cap: 5);
        Assert.Equal(5, await _store.CountUnsentAsync());
    }

    [Fact]
    public async Task PruneUnsentToCapAsync_DoesNothingWhenUnderCap()
    {
        await StoreRows(3);
        await _store.PruneUnsentToCapAsync(cap: 10);
        Assert.Equal(3, await _store.CountUnsentAsync());
    }

    [Fact]
    public async Task PruneUnsentToCapAsync_KeepsNewestRows()
    {
        var base_ = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
            await _store.StoreAsync(MakeRow(base_.AddSeconds(i * 30)));

        await _store.PruneUnsentToCapAsync(cap: 2);

        var remaining = await _store.GetAllAsync(limit: 10);
        Assert.Equal(2, remaining.Count);
        Assert.Equal(base_.AddSeconds(3 * 30).ToString("O"), remaining[0].WindowStartTs.ToString("O"));
        Assert.Equal(base_.AddSeconds(4 * 30).ToString("O"), remaining[1].WindowStartTs.ToString("O"));
    }

    private async Task StoreRows(int count)
    {
        var base_ = DateTimeOffset.UtcNow;
        for (int i = 0; i < count; i++)
            await _store.StoreAsync(MakeRow(base_.AddSeconds(i * 30)));
    }

    private static FeatureRow MakeRow(DateTimeOffset windowStart) =>
        FeatureRow.CreateNew(
            deviceId: "test-device",
            windowSec: 60,
            windowStartTs: windowStart,
            featureVersion: "1.2",
            windowProfileId: "W60_S30",
            windowSizeSec: 60,
            slideSec: 30,
            eventTimeStart: windowStart,
            eventTimeEnd: windowStart.AddSeconds(60),
            extractionRunId: "run-test",
            featureSchemaVersion: "1.2",
            collectorSchemaVersion: null,
            sourceCounts: new Dictionary<string, int>(),
            features: new Dictionary<string, object>());
}
