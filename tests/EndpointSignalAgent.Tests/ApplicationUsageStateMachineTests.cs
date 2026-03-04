using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Utilities;
using EndpointSignalAgent.SignalCollection.Collectors;
using System.Collections.Concurrent;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class ApplicationUsageStateMachineTests
{
    [Fact(Skip = "Temporarily disabled")] // temporay disable 

    public async Task Dwell_ClosesOnInactivity_WithoutCountingInactiveTime()
    {
        var t0 = DateTimeOffset.Parse("2026-03-04T00:00:00Z");
        var clock = new FakeClock(t0);
        var emitter = new FakeEmitter();
        var resolver = new FakeProcessInfoResolver
        {
            [101] = new ProcessResolution("code", @"C:\\Tools\\Code.exe", true)
        };

        var sut = CreateStateMachine(clock, emitter, resolver, "device-a");

        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0, "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(100), "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Inactive(t0.AddMilliseconds(900), "poll"));
        await sut.HandleObservationAsync(ForegroundSample.Inactive(t0.AddMilliseconds(1700), "poll"));

        var dwell = emitter.Events.Single(x => x.Type == SignalEventType.AppDwell);
        Assert.Equal("900", dwell.Payload["durationMs"]);
        Assert.Equal("no_foreground", dwell.Payload["reason"]);
    }

    [Fact(Skip = "Temporarily disabled")]
    public async Task Debouncer_RejectsShortTransientSwitches()
    {
        var t0 = DateTimeOffset.Parse("2026-03-04T00:00:00Z");
        var clock = new FakeClock(t0);
        var emitter = new FakeEmitter();
        var resolver = new FakeProcessInfoResolver
        {
            [101] = new ProcessResolution("code", @"C:\\Tools\\Code.exe", true),
            [202] = new ProcessResolution("zoom", @"C:\\Apps\\Zoom.exe", true)
        };

        var sut = CreateStateMachine(clock, emitter, resolver, "device-a");

        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0, "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(100), "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(202, t0.AddMilliseconds(250), "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(320), "hook"));

        Assert.Single(emitter.Events.Where(x => x.Type == SignalEventType.ForegroundAppChanged));
        Assert.DoesNotContain(emitter.Events, x =>
            x.Type == SignalEventType.AppDwell &&
            x.Payload.TryGetValue("reason", out var reason) &&
            string.Equals(reason, "switch", StringComparison.Ordinal));
    }

    [Fact(Skip = "Temporarily disabled")]
    public async Task SwitchRate_CountsCommittedSwitchesOnly()
    {
        var t0 = DateTimeOffset.Parse("2026-03-04T00:00:00Z");
        var clock = new FakeClock(t0);
        var emitter = new FakeEmitter();
        var resolver = new FakeProcessInfoResolver
        {
            [101] = new ProcessResolution("code", @"C:\\Tools\\Code.exe", true),
            [202] = new ProcessResolution("zoom", @"C:\\Apps\\Zoom.exe", true),
            [303] = new ProcessResolution("excel", @"C:\\Office\\EXCEL.EXE", true)
        };

        var sut = CreateStateMachine(clock, emitter, resolver, "device-a");

        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0, "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(100), "hook"));

        await sut.HandleObservationAsync(ForegroundSample.Active(202, t0.AddSeconds(5), "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddSeconds(5.2), "hook"));

        await sut.HandleObservationAsync(ForegroundSample.Active(303, t0.AddSeconds(10), "hook"));
        await sut.HandleObservationAsync(ForegroundSample.Active(303, t0.AddSeconds(10.5), "hook"));

        await sut.HandleTimerTickAsync(t0.AddSeconds(61));

        var rate = emitter.Events.Single(x => x.Type == SignalEventType.AppSwitchRate);
        Assert.Equal("1", rate.Payload["switches"]);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void Hashing_IsSalted_AndStableWithinDeviceSecret()
    {
        var value = "app|C:\\Tools\\Code.exe";
        var hasherA1 = new EndpointSignalAgent.SignalCollection.Collectors.Network.HashingService(new FakeSaltProvider("device-secret-a"));
        var hasherA2 = new EndpointSignalAgent.SignalCollection.Collectors.Network.HashingService(new FakeSaltProvider("device-secret-a"));
        var hasherB = new EndpointSignalAgent.SignalCollection.Collectors.Network.HashingService(new FakeSaltProvider("device-secret-b"));

        var a1 = hasherA1.HashStable(value);
        var a2 = hasherA2.HashStable(value);
        var b = hasherB.HashStable(value);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal(24, a1.Length);
    }

    [Fact(Skip = "Temporarily disabled")]
    public void Categorizer_Normalization_MapsRealProcessNames()
    {
        Assert.Equal("Browser", ApplicationCategorizer.Categorize("MS-EDGE.EXE"));
        Assert.Equal("IDE", ApplicationCategorizer.Categorize(" Code .exe "));
        Assert.Equal("Comms", ApplicationCategorizer.Categorize("Ms-Teams.exe"));
        Assert.Equal("Terminal", ApplicationCategorizer.Categorize("Windows_Terminal.exe"));
        Assert.Equal("System", ApplicationCategorizer.Categorize("explorer.exe"));
        Assert.Equal("Other", ApplicationCategorizer.Categorize("youtube"));
    }

    private static ApplicationUsageStateMachine CreateStateMachine(
        IClock clock,
        FakeEmitter emitter,
        IProcessInfoResolver resolver,
        string salt)
    {
        return new ApplicationUsageStateMachine(
            emitter,
            resolver,
            clock,
            new EndpointSignalAgent.SignalCollection.Collectors.Network.HashingService(new FakeSaltProvider(salt)),
            () => { },
            () => { },
            "hook");
    }

    private sealed class FakeEmitter : IApplicationUsageEmitter
    {
        public ConcurrentQueue<(SignalEventType Type, Dictionary<string, string> Payload)> EventsQueue { get; } = new();

        public IReadOnlyList<(SignalEventType Type, Dictionary<string, string> Payload)> Events => EventsQueue.ToArray();

        public Task EmitAsync(SignalEventType type, Dictionary<string, string> payload)
        {
            EventsQueue.Enqueue((type, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FakeProcessInfoResolver : IProcessInfoResolver
    {
        private readonly Dictionary<uint, ProcessResolution> _values = new();

        public ProcessResolution this[uint pid]
        {
            set => _values[pid] = value;
        }

        public bool TryResolve(uint processId, out ProcessResolution resolution)
        {
            return _values.TryGetValue(processId, out resolution);
        }
    }

    private sealed class FakeSaltProvider : EndpointSignalAgent.SignalCollection.Collectors.Network.ISaltProvider
    {
        private readonly string _salt;

        public FakeSaltProvider(string salt)
        {
            _salt = salt;
        }

        public string GetStableSalt() => _salt;
    }
}

