# AppDwell Open-Dwell Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `app_top1_share`, `cat_*_ratio`, `app_dwell_*`, and `has_app_data` being zero during sustained single-app focus by introducing a periodic `AppFocusHeartbeat` that lets the extractor synthesize the open (unclosed) dwell segment.

**Architecture:** The `ApplicationUsageStateMachine` emits `AppFocusHeartbeat` every 15 seconds while a dwell is open. The `AppFeatureAggregator` checks for an unclosed heartbeat after processing all `AppDwell` events and synthesizes a virtual overlap segment from `dwellStartUtc` to `window.EndUtc` when no closing `AppDwell` exists. The 15-second interval guarantees at least one heartbeat falls within the `historyStart` context window for every supported profile (W30S15 lookback = 45s, W60S30 lookback = 90s).

**Tech Stack:** C# 12, .NET 8, xUnit — no new dependencies.

---

## Root cause recap

`AppDwell` fires only when a dwell **ends** (`"switch"`, `"no_foreground"`, `"shutdown_flush"`). The extractor's `AppFeatureAggregator.BuildOverlapSegments` only processes `AppDwell` events, so during 13 minutes of focused work in one app, every 30-second window sees `overlapSegments = []` and sets `app_top1_share = 0`, `has_app_data = 0`, all `cat_*_ratio = 0`.

## Trigger-fault scan results

| Collector | Signal | Pattern | Verdict |
|---|---|---|---|
| `ApplicationUsageCollector` | `AppDwell` | Trailing-edge (fires at dwell end) | **BUG — fixing here** |
| `SessionStateCollector` | `SessionLock/Unlock`, `IdleSample`, display events | State-transition / periodic boundary-crossing | OK — `BuildInitialState` reconstructs pre-window state |
| `NetworkContextCollector` | `VpnStateChanged`, `WifiLinkChanged`, etc. | State-transition | OK — ratio model handles correctly |
| `SystemResourceCollector` | `SystemResourceTick` | Periodic sampling | OK |
| `FeatureExtractorService.CompactBufferLocked` | All preserved state types | Keeps latest-by-type | Latent risk: for **very** long single-state sessions (e.g., locked overnight), the preserved event's timestamp falls before `historyStart` and is excluded from context. `BuildInitialState` then defaults to unlocked. Low frequency, low impact — document as follow-up issue, not fixed here. |

---

## File map

| File | Change |
|---|---|
| `src/Shared/Contracts/SignalEvent.cs` | Add `AppFocusHeartbeat` to `SignalEventType` enum |
| `src/SignalCollection/Collectors/ApplicationUsageCollector.cs` | Emit `AppFocusHeartbeat` every 15s in `HandleTimerTickAsync`; reset on dwell close |
| `src/FeatureExtraction/SignalAggregator/Windowing.cs` | Add `PayloadValueReader.TryGetDateTimeOffset` |
| `src/FeatureExtraction/SignalAggregator/AppFeatureAggregator.cs` | Synthesize open-dwell segment from last heartbeat in `BuildOverlapSegments`; update `has_app_data` |
| `src/FeatureExtraction/Services/FeatureExtractorService.cs` | Add `AppFocusHeartbeat` to `preserveStateTypes` in `CompactBufferLocked`; add to `BuildSourceCounts` |
| `tests/EndpointSignalAgent.Tests/ApplicationUsageStateMachineTests.cs` | Remove `Skip` attributes; add heartbeat emission tests |
| `tests/EndpointSignalAgent.Tests/WindowedFeatureExtractionTests.cs` | Add sustained-focus, open-dwell synthesis, and no-double-count tests |

---

## Task 1: Add `AppFocusHeartbeat` to `SignalEventType`

**Files:**
- Modify: `src/Shared/Contracts/SignalEvent.cs`

- [ ] **Step 1: Add the enum value**

In `SignalEvent.cs`, add `AppFocusHeartbeat` after `AppSwitchRate` (line 30):

```csharp
AppSwitchRate,

AppFocusHeartbeat,
```

The full enum after the change (lines 13–47):
```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalEventType
{
    Unknown = 0,
    Heartbeat,
    SessionLock,
    SessionUnlock,
    IdleSample,
    ForegroundAppChanged,
    AppDwell,
    AppSwitchRate,
    AppFocusHeartbeat,
    LocalNetworkChanged,
    VpnStateChanged,
    WifiLinkChanged,
    ScreenSaverOn,
    ScreenSaverOff,
    DisplayOn,
    DisplayOff,
    DisplayDimmed,
    WifiSsidChanged,
    PublicIpBucketChanged,
    SystemResourceTick,
    PowerSuspend,
    PowerResume,
    CollectionGapDetected
}
```

- [ ] **Step 2: Build to confirm no compiler errors**

```
dotnet build EndpointSignalAgent.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```
git add src/Shared/Contracts/SignalEvent.cs
git commit -m "feat: add AppFocusHeartbeat signal type"
```

---

## Task 2: Emit `AppFocusHeartbeat` from `ApplicationUsageStateMachine`

**Files:**
- Modify: `src/SignalCollection/Collectors/ApplicationUsageCollector.cs`

- [ ] **Step 1: Write the failing test**

In `tests/EndpointSignalAgent.Tests/ApplicationUsageStateMachineTests.cs`, add (inside the class, before the helpers):

```csharp
[Fact]
public async Task FocusHeartbeat_EmittedAfter15Seconds_WhenDwellIsOpen()
{
    var t0 = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var clock = new FakeClock(t0);
    var emitter = new FakeEmitter();
    var resolver = new FakeProcessInfoResolver
    {
        [101] = new ProcessResolution("code", @"C:\Tools\Code.exe", true)
    };
    var sut = CreateStateMachine(clock, emitter, resolver, "device-a");

    // Commit app 101 as current
    await sut.HandleObservationAsync(ForegroundSample.Active(101, t0, "hook"));
    await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(500), "hook"));

    // Tick at +5s — no heartbeat yet (< 15s)
    await sut.HandleTimerTickAsync(t0.AddSeconds(5));
    Assert.DoesNotContain(emitter.Events, e => e.Type == SignalEventType.AppFocusHeartbeat);

    // Tick at +16s — heartbeat should fire
    await sut.HandleTimerTickAsync(t0.AddSeconds(16));
    var heartbeat = Assert.Single(emitter.Events.Where(e => e.Type == SignalEventType.AppFocusHeartbeat));
    Assert.Equal(heartbeat.Payload["appKey"], emitter.Events.First(e => e.Type == SignalEventType.ForegroundAppChanged).Payload["appKey"]);
    Assert.True(DateTimeOffset.TryParse(heartbeat.Payload["dwellStartUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
}

[Fact]
public async Task FocusHeartbeat_EmittedEvery15Seconds_DuringLongDwell()
{
    var t0 = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var clock = new FakeClock(t0);
    var emitter = new FakeEmitter();
    var resolver = new FakeProcessInfoResolver
    {
        [101] = new ProcessResolution("code", @"C:\Tools\Code.exe", true)
    };
    var sut = CreateStateMachine(clock, emitter, resolver, "device-a");

    await sut.HandleObservationAsync(ForegroundSample.Active(101, t0, "hook"));
    await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(500), "hook"));

    // Tick every second for 60 seconds
    for (var sec = 1; sec <= 60; sec++)
    {
        await sut.HandleTimerTickAsync(t0.AddSeconds(sec));
    }

    // Expect heartbeats at roughly 15s, 30s, 45s, 60s (±1 tick) — at least 3
    var heartbeats = emitter.Events.Where(e => e.Type == SignalEventType.AppFocusHeartbeat).ToList();
    Assert.True(heartbeats.Count >= 3, $"Expected ≥3 heartbeats in 60s, got {heartbeats.Count}");
}

[Fact]
public async Task FocusHeartbeat_NotEmittedAfterDwellCloses()
{
    var t0 = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var clock = new FakeClock(t0);
    var emitter = new FakeEmitter();
    var resolver = new FakeProcessInfoResolver
    {
        [101] = new ProcessResolution("code", @"C:\Tools\Code.exe", true),
        [202] = new ProcessResolution("firefox", @"C:\Firefox\firefox.exe", true)
    };
    var sut = CreateStateMachine(clock, emitter, resolver, "device-a");

    // Commit app 101
    await sut.HandleObservationAsync(ForegroundSample.Active(101, t0, "hook"));
    await sut.HandleObservationAsync(ForegroundSample.Active(101, t0.AddMilliseconds(500), "hook"));

    // Switch to app 202 — commits AppDwell for 101
    await sut.HandleObservationAsync(ForegroundSample.Active(202, t0.AddSeconds(5), "hook"));
    await sut.HandleObservationAsync(ForegroundSample.Active(202, t0.AddSeconds(5.5), "hook"));

    var countBeforeTick = emitter.Events.Count(e => e.Type == SignalEventType.AppFocusHeartbeat);

    // Wait 16s after switch — heartbeat for app 202 is OK, but there should be none for app 101
    await sut.HandleTimerTickAsync(t0.AddSeconds(22));

    var heartbeats = emitter.Events.Where(e => e.Type == SignalEventType.AppFocusHeartbeat).ToList();
    // All heartbeats after the switch should carry app 202's key
    var app101Key = emitter.Events
        .First(e => e.Type == SignalEventType.ForegroundAppChanged)
        .Payload["appKey"];
    Assert.DoesNotContain(heartbeats, h => h.Payload["appKey"] == app101Key);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test --filter "FocusHeartbeat" -v normal
```

Expected: FAIL — `AppFocusHeartbeat` is not emitted yet.

- [ ] **Step 3: Add `_lastHeartbeatUtc` field and heartbeat emission to the state machine**

In `ApplicationUsageCollector.cs`, in the `ApplicationUsageStateMachine` class:

Add field after `_windowStart` (line 289):
```csharp
private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
```

Replace `HandleTimerTickAsync` (lines 292–295):
```csharp
public async Task HandleTimerTickAsync(DateTimeOffset nowUtc)
{
    await EmitSwitchRateIfWindowElapsedAsync(nowUtc);
    await EmitHeartbeatIfDueAsync(nowUtc);
}
```

Add `EmitHeartbeatIfDueAsync` as a new private method after `EmitSwitchRateIfWindowElapsedAsync`:
```csharp
private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

private async Task EmitHeartbeatIfDueAsync(DateTimeOffset nowUtc)
{
    if (_current is null)
    {
        return;
    }

    if ((nowUtc - _lastHeartbeatUtc) < HeartbeatInterval)
    {
        return;
    }

    _lastHeartbeatUtc = nowUtc;

    await _emitter.EmitAsync(SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
    {
        ["appKey"] = _current.Value.App.AppKey,
        ["category"] = _current.Value.App.Category,
        ["confidence"] = _current.Value.App.Confidence,
        ["dwellStartUtc"] = _current.Value.SinceUtc.ToString("O")
    });
}
```

Reset `_lastHeartbeatUtc` when dwell closes. In `HandleInactiveAsync` (line 370, after `_current = null`):
```csharp
await EmitDwellAsync(_current.Value.App, _current.Value.SinceUtc, _inactiveSince, "no_foreground", sample.CollectorMode);
_current = null;
_inactiveClosedCurrent = true;
_lastHeartbeatUtc = DateTimeOffset.MinValue;
```

In `TryCommitPendingAsync` (line 436, after `await EmitDwellAsync(...)`):
```csharp
await EmitDwellAsync(_current.Value.App, _current.Value.SinceUtc, pending.FirstSeenUtc, "switch", pending.CollectorMode);
_switchesInWindow++;
_lastHeartbeatUtc = DateTimeOffset.MinValue;
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test --filter "FocusHeartbeat" -v normal
```

Expected: PASS (all 3 new tests).

- [ ] **Step 5: Build full project**

```
dotnet build EndpointSignalAgent.csproj
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```
git add src/SignalCollection/Collectors/ApplicationUsageCollector.cs
git add tests/EndpointSignalAgent.Tests/ApplicationUsageStateMachineTests.cs
git commit -m "feat: emit AppFocusHeartbeat every 15s during open dwell"
```

---

## Task 3: Add `TryGetDateTimeOffset` to `PayloadValueReader`

**Files:**
- Modify: `src/FeatureExtraction/SignalAggregator/Windowing.cs`

- [ ] **Step 1: Write the failing test**

In `WindowedFeatureExtractionTests.cs`, add:

```csharp
[Fact]
public void PayloadValueReader_TryGetDateTimeOffset_ParsesRoundTripFormat()
{
    var expected = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var payload = new Dictionary<string, string>
    {
        ["ts"] = expected.ToString("O")
    };

    var found = PayloadValueReader.TryGetDateTimeOffset(payload, "ts", out var actual);

    Assert.True(found);
    Assert.Equal(expected, actual);
}

[Fact]
public void PayloadValueReader_TryGetDateTimeOffset_ReturnsFalseForMissingKey()
{
    var payload = new Dictionary<string, string>();
    Assert.False(PayloadValueReader.TryGetDateTimeOffset(payload, "ts", out _));
}
```

- [ ] **Step 2: Run to confirm FAIL**

```
dotnet test --filter "TryGetDateTimeOffset" -v normal
```

Expected: FAIL — method does not exist.

- [ ] **Step 3: Add the method to `PayloadValueReader` in `Windowing.cs`**

Add after the `GetDouble` method (after line 176):
```csharp
public static bool TryGetDateTimeOffset(IReadOnlyDictionary<string, string> payload, string key, out DateTimeOffset value)
{
    value = default;
    if (!payload.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
    {
        return false;
    }

    return DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out value);
}
```

- [ ] **Step 4: Run tests**

```
dotnet test --filter "TryGetDateTimeOffset" -v normal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```
git add src/FeatureExtraction/SignalAggregator/Windowing.cs
git add tests/EndpointSignalAgent.Tests/WindowedFeatureExtractionTests.cs
git commit -m "feat: add PayloadValueReader.TryGetDateTimeOffset"
```

---

## Task 4: Preserve `AppFocusHeartbeat` in buffer compaction and count it in source counts

**Files:**
- Modify: `src/FeatureExtraction/Services/FeatureExtractorService.cs`

- [ ] **Step 1: Add `AppFocusHeartbeat` to `preserveStateTypes` in `CompactBufferLocked`**

In `FeatureExtractorService.cs`, inside `CompactBufferLocked` (line 318), add `SignalEventType.AppFocusHeartbeat` to the `preserveStateTypes` set:

```csharp
var preserveStateTypes = new HashSet<SignalEventType>
{
    SignalEventType.AppFocusHeartbeat,  // ← add this line
    SignalEventType.SessionLock,
    SignalEventType.SessionUnlock,
    SignalEventType.DisplayOn,
    SignalEventType.DisplayOff,
    SignalEventType.DisplayDimmed,
    SignalEventType.ScreenSaverOn,
    SignalEventType.ScreenSaverOff,
    SignalEventType.IdleSample,
    SignalEventType.VpnStateChanged,
    SignalEventType.WifiLinkChanged,
    SignalEventType.WifiSsidChanged,
    SignalEventType.PublicIpBucketChanged,
    SignalEventType.PowerSuspend,
    SignalEventType.PowerResume,
    SignalEventType.CollectionGapDetected
};
```

- [ ] **Step 2: Add `AppFocusHeartbeat` to `BuildSourceCounts`**

In `BuildSourceCounts` (lines 555–564), add `SignalEventType.AppFocusHeartbeat` to the `"application"` count:

```csharp
private static Dictionary<string, int> BuildSourceCounts(IReadOnlyList<FeatureSignal> context)
{
    return new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["application"] = context.Count(s => s.Type is
            SignalEventType.ForegroundAppChanged or
            SignalEventType.AppDwell or
            SignalEventType.AppSwitchRate or
            SignalEventType.AppFocusHeartbeat),
        ["session"] = context.Count(s => s.Type is
            SignalEventType.SessionLock or
            SignalEventType.SessionUnlock or
            SignalEventType.IdleSample or
            SignalEventType.ScreenSaverOn or
            SignalEventType.ScreenSaverOff or
            SignalEventType.DisplayOn or
            SignalEventType.DisplayOff or
            SignalEventType.DisplayDimmed),
        ["network"] = context.Count(s => s.Type is
            SignalEventType.VpnStateChanged or
            SignalEventType.WifiLinkChanged or
            SignalEventType.WifiSsidChanged or
            SignalEventType.LocalNetworkChanged or
            SignalEventType.PublicIpBucketChanged),
        ["system"] = context.Count(s => s.Type is SignalEventType.SystemResourceTick)
    };
}
```

- [ ] **Step 3: Build**

```
dotnet build EndpointSignalAgent.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```
git add src/FeatureExtraction/Services/FeatureExtractorService.cs
git commit -m "feat: preserve AppFocusHeartbeat in buffer compaction; count in source counts"
```

---

## Task 5: Synthesize open-dwell segment in `AppFeatureAggregator`

**Files:**
- Modify: `src/FeatureExtraction/SignalAggregator/AppFeatureAggregator.cs`

- [ ] **Step 1: Write the failing tests**

In `WindowedFeatureExtractionTests.cs`, add:

```csharp
[Fact]
public void AppFeatures_SustainedFocus_ProducesNonZeroTop1Share()
{
    // User has been in "code" for 2 minutes with no app switch.
    // Only an AppFocusHeartbeat is present in context — no AppDwell.
    var aggregator = new AppFeatureAggregator();
    var windowStart = DateTimeOffset.Parse("2026-06-11T10:02:00Z");
    var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(30));

    var dwellStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z"); // 2 min before window

    var events = new List<FeatureSignal>
    {
        // Heartbeat emitted 5s before window end (well within historyStart context)
        new(windowStart + TimeSpan.FromSeconds(25), SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
        {
            ["appKey"] = "abc123",
            ["category"] = "IDE",
            ["confidence"] = "high",
            ["dwellStartUtc"] = dwellStart.ToString("O")
        })
    };

    var result = aggregator.ExtractFeatures(events, window);

    Assert.Equal(1.0, result.Features["app_top1_share"], 3);
    Assert.Equal(1.0, result.Features["has_app_data"], 3);
    Assert.True(result.Features["cat_ide_ratio"] > 0.0, "cat_ide_ratio should be > 0 during sustained IDE focus");
    Assert.Equal(1.0, result.Features["app_confidence_high_ratio"], 3);
}

[Fact]
public void AppFeatures_OpenDwell_ClipsToWindowEnd()
{
    // Dwell started in the middle of the window — only part of the window is covered.
    var aggregator = new AppFeatureAggregator();
    var windowStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));

    var dwellStart = windowStart + TimeSpan.FromSeconds(20); // dwell starts 20s into window

    var events = new List<FeatureSignal>
    {
        new(windowStart + TimeSpan.FromSeconds(50), SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
        {
            ["appKey"] = "abc123",
            ["category"] = "Browser",
            ["confidence"] = "high",
            ["dwellStartUtc"] = dwellStart.ToString("O")
        })
    };

    var result = aggregator.ExtractFeatures(events, window);

    // 40s of dwell out of 60s window
    Assert.Equal(40.0 / 60.0, result.Features["cat_browser_ratio"], 3);
    Assert.Equal(1.0, result.Features["app_top1_share"], 3);
}

[Fact]
public void AppFeatures_NoDoubleCount_WhenRealDwellAndHeartbeatBothPresent()
{
    // AppDwell event closes the dwell — heartbeat should NOT add a synthetic segment.
    var aggregator = new AppFeatureAggregator();
    var windowStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));

    var dwellStart = windowStart - TimeSpan.FromSeconds(10);
    var dwellEnd = windowStart + TimeSpan.FromSeconds(40);

    var events = new List<FeatureSignal>
    {
        // Real AppDwell: 50s total, 40s overlap with window
        new(dwellEnd, SignalEventType.AppDwell, new Dictionary<string, string>
        {
            ["appKey"] = "abc123",
            ["category"] = "IDE",
            ["durationMs"] = "50000",
            ["confidence"] = "high",
            ["reason"] = "switch"
        }),
        // Heartbeat present in context (emitted before the switch closed the dwell)
        new(dwellEnd - TimeSpan.FromSeconds(5), SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
        {
            ["appKey"] = "abc123",
            ["category"] = "IDE",
            ["confidence"] = "high",
            ["dwellStartUtc"] = dwellStart.ToString("O")
        })
    };

    var result = aggregator.ExtractFeatures(events, window);

    // Should be exactly 40s / 60s — the real AppDwell, NOT 40s + synthetic (window.EndUtc - dwellStart)
    Assert.Equal(40.0 / 60.0, result.Features["cat_ide_ratio"], 3);
}

[Fact]
public void AppFeatures_NoHeartbeat_NoSyntheticSegment_BackwardCompat()
{
    // Old events (no heartbeat) — no open-dwell synthesis, no regression.
    var aggregator = new AppFeatureAggregator();
    var windowStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
    var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));

    var events = new List<FeatureSignal>(); // empty — simulates sustained focus, no heartbeat

    var result = aggregator.ExtractFeatures(events, window);

    Assert.Equal(0.0, result.Features["app_top1_share"]);
    Assert.Equal(0.0, result.Features["has_app_data"]);
}
```

- [ ] **Step 2: Run to confirm FAIL**

```
dotnet test --filter "AppFeatures_SustainedFocus|AppFeatures_OpenDwell|AppFeatures_NoDoubleCount|AppFeatures_NoHeartbeat" -v normal
```

Expected: FAIL — `AppFeatures_SustainedFocus`, `AppFeatures_OpenDwell`, and `AppFeatures_NoDoubleCount` fail; `AppFeatures_NoHeartbeat_NoSyntheticSegment_BackwardCompat` passes.

- [ ] **Step 3: Update `AppFeatureAggregator` to include `AppFocusHeartbeat` in event filtering**

In `AppFeatureAggregator.cs`, update the `appEvents` filter (line 17) to include `AppFocusHeartbeat`:

```csharp
var appEvents = events
    .Where(e => e.Type == SignalEventType.AppDwell
             || e.Type == SignalEventType.ForegroundAppChanged
             || e.Type == SignalEventType.AppSwitchRate
             || e.Type == SignalEventType.AppFocusHeartbeat)
    .OrderBy(e => e.TimestampUtc)
    .ToList();
```

- [ ] **Step 4: Add open-dwell synthesis to `BuildOverlapSegments`**

In `AppFeatureAggregator.cs`, replace `BuildOverlapSegments` entirely:

```csharp
private static List<AppOverlapSegment> BuildOverlapSegments(IReadOnlyList<FeatureSignal> appEvents, SlidingWindow window)
{
    var overlaps = new List<AppOverlapSegment>();
    var targetWindows = new[] { window };

    foreach (var evt in appEvents)
    {
        if (evt.Type != SignalEventType.AppDwell)
        {
            continue;
        }

        if (!PayloadValueReader.TryGetLong(evt.Payload, "durationMs", out var durationMs) || durationMs <= 0)
        {
            continue;
        }

        var segEnd = evt.TimestampUtc;
        var segStart = segEnd - TimeSpan.FromMilliseconds(durationMs);
        var appKey = PayloadValueReader.GetString(evt.Payload, "appKey", "unknown");
        var category = NormalizeCategory(PayloadValueReader.GetString(evt.Payload, "category", "Other"));
        var confidenceHigh = string.Equals(PayloadValueReader.GetString(evt.Payload, "confidence", "low"), "high", StringComparison.OrdinalIgnoreCase);

        var split = SlidingWindowing.SplitSegmentAcrossWindows(segStart, segEnd, targetWindows);
        foreach (var item in split)
        {
            overlaps.Add(new AppOverlapSegment(appKey, category, item.OverlapStartUtc, item.OverlapEndUtc, item.OverlapMs, confidenceHigh));
        }
    }

    // Synthesize open-dwell segment from the most recent AppFocusHeartbeat,
    // but only when no AppDwell has already closed that dwell.
    var lastHeartbeat = appEvents
        .Where(e => e.Type == SignalEventType.AppFocusHeartbeat)
        .OrderByDescending(e => e.TimestampUtc)
        .FirstOrDefault();

    if (lastHeartbeat.Payload is not null &&
        PayloadValueReader.TryGetDateTimeOffset(lastHeartbeat.Payload, "dwellStartUtc", out var openDwellStartUtc) &&
        openDwellStartUtc < window.EndUtc)
    {
        var heartbeatAppKey = PayloadValueReader.GetString(lastHeartbeat.Payload, "appKey", "unknown");

        var dwellAlreadyClosed = appEvents.Any(e =>
            e.Type == SignalEventType.AppDwell &&
            string.Equals(PayloadValueReader.GetString(e.Payload, "appKey", ""), heartbeatAppKey, StringComparison.Ordinal) &&
            e.TimestampUtc > openDwellStartUtc);

        if (!dwellAlreadyClosed)
        {
            var openCategory = NormalizeCategory(PayloadValueReader.GetString(lastHeartbeat.Payload, "category", "Other"));
            var openConfidenceHigh = string.Equals(PayloadValueReader.GetString(lastHeartbeat.Payload, "confidence", "low"), "high", StringComparison.OrdinalIgnoreCase);

            var split = SlidingWindowing.SplitSegmentAcrossWindows(openDwellStartUtc, window.EndUtc, targetWindows);
            foreach (var item in split)
            {
                overlaps.Add(new AppOverlapSegment(heartbeatAppKey, openCategory, item.OverlapStartUtc, item.OverlapEndUtc, item.OverlapMs, openConfidenceHigh));
            }
        }
    }

    return overlaps;
}
```

- [ ] **Step 5: Update `has_app_data` to include the heartbeat-only case**

In `ExtractFeatures`, replace the `has_app_data` line (line 28):

```csharp
var hasOpenDwell = appEvents.Any(e => e.Type == SignalEventType.AppFocusHeartbeat);
features["has_app_data"] = (overlapSegments.Count > 0 || windowEvents.Count > 0 || hasOpenDwell) ? 1.0 : 0.0;
```

- [ ] **Step 6: Run the new tests**

```
dotnet test --filter "AppFeatures_SustainedFocus|AppFeatures_OpenDwell|AppFeatures_NoDoubleCount|AppFeatures_NoHeartbeat" -v normal
```

Expected: PASS (all 4 tests).

- [ ] **Step 7: Run all tests**

```
dotnet test -v normal
```

Expected: all previously-passing tests still pass.

- [ ] **Step 8: Commit**

```
git add src/FeatureExtraction/SignalAggregator/AppFeatureAggregator.cs
git add tests/EndpointSignalAgent.Tests/WindowedFeatureExtractionTests.cs
git commit -m "feat: synthesize open-dwell segment from AppFocusHeartbeat in AppFeatureAggregator"
```

---

## Task 6: Re-enable and fix the skipped state machine tests

**Files:**
- Modify: `tests/EndpointSignalAgent.Tests/ApplicationUsageStateMachineTests.cs`

The existing tests are correct but were skipped. The new heartbeat emission in `HandleTimerTickAsync` may affect `SwitchRate_CountsCommittedSwitchesOnly` (since ticks now also trigger heartbeat checks). Verify and fix if needed.

- [ ] **Step 1: Remove `Skip` from all four existing tests**

In `ApplicationUsageStateMachineTests.cs`, remove all four `[Fact(Skip = "Temporarily disabled")]` attributes, replacing each with `[Fact]`:

Lines 11, 35, 61, 91:
```csharp
[Fact]
public async Task Dwell_ClosesOnInactivity_WithoutCountingInactiveTime()
```
```csharp
[Fact]
public async Task Debouncer_RejectsShortTransientSwitches()
```
```csharp
[Fact]
public async Task SwitchRate_CountsCommittedSwitchesOnly()
```
```csharp
[Fact]
public void Hashing_IsSalted_AndStableWithinDeviceSecret()
```

- [ ] **Step 2: Run the restored tests**

```
dotnet test --filter "Dwell_ClosesOnInactivity|Debouncer_RejectsShortTransient|SwitchRate_CountsCommitted|Hashing_IsSalted|Categorizer_Normalization" -v normal
```

Expected: all 5 PASS. If `SwitchRate_CountsCommittedSwitchesOnly` fails due to extra heartbeat events in the emitter, adjust the assertion to filter only `AppSwitchRate` events (the test already does `Single(x => x.Type == SignalEventType.AppSwitchRate)` so it should be unaffected).

- [ ] **Step 3: Run full test suite**

```
dotnet test -v normal
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```
git add tests/EndpointSignalAgent.Tests/ApplicationUsageStateMachineTests.cs
git commit -m "test: re-enable ApplicationUsageStateMachine tests; add FocusHeartbeat tests"
```

---

## Follow-up issues (out of scope for this plan)

These were found during the trigger-fault scan. File separate issues — do not fix here.

### Latent session state reconstruction gap

**Where:** `FeatureExtractorService.CompactBufferLocked` + `SessionFeatureAggregator.BuildInitialState`

**Symptom:** If a machine has been in a single session state (locked, display off) for longer than `historyStart` lookback (~90s for W60S30), the preserved state event has an old timestamp and is excluded from the context window filter in `BuildWindowJobsLocked`. `BuildInitialState` then defaults to `SessionState.Default` (unlocked, display on), producing wrong `locked_ratio` / `display_off_ratio` for long-duration single-state sessions.

**Proposed fix (when prioritised):** Change `BuildWindowJobsLocked` to also append the preserved state events (timestamp < historyStart) to context before filtering, or widen historyStart for state reconstruction purposes.

**Frequency:** Low — only affects machines locked/locked-display for >90s without a new state event (suspend/resume would have reset warm-up; most lock/unlock cycles are <90s apart).

---

## Self-review checklist

- [x] **Spec coverage**: AppDwell trailing-edge → fixed via heartbeat + synthesis. All affected features (`app_top1_share`, `cat_*_ratio`, `app_dwell_*`, `has_app_data`) are now sourced from overlaps that include the open-dwell synthetic segment.
- [x] **Scan result coverage**: All four collectors scanned. Two additional patterns noted (one latent session bug documented as follow-up).
- [x] **No placeholders**: All code blocks are complete, not sketched.
- [x] **Type consistency**: `AppOverlapSegment`, `FeatureSignal`, `PayloadValueReader`, `SlidingWindowing.SplitSegmentAcrossWindows` — all used with the same signatures as defined in `FeatureResultModels.cs` and `Windowing.cs`.
- [x] **Double-count guard**: Tests `AppFeatures_NoDoubleCount_WhenRealDwellAndHeartbeatBothPresent` covers this path.
- [x] **Backward compat**: `AppFeatures_NoHeartbeat_NoSyntheticSegment_BackwardCompat` verifies old events without heartbeats produce the same output as before.
- [x] **`_lastHeartbeatUtc` reset**: Reset on both `"no_foreground"` close (in `HandleInactiveAsync`) and `"switch"` close (in `TryCommitPendingAsync`). `FlushShutdownAsync` does not need reset since the state machine is discarded after flush.
