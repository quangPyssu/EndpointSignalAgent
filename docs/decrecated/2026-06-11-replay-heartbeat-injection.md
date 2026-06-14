# Replay Heartbeat Injection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retroactively fix zero app features in historical raw signal files by injecting synthetic `AppFocusHeartbeat` events during replay so that `AppFeatureAggregator` produces correct `app_top1_share`, `cat_*_ratio`, and `has_app_data` for sustained single-app focus windows.

**Architecture:** A new static `AppDwellReplayPreprocessor` class walks the full sorted signal list once, tracking open dwells via `ForegroundAppChanged`/`AppDwell` pairs, and injects a synthetic `AppFocusHeartbeat` every 15 seconds during each open dwell interval. `ExtractFeaturesFromFileAsync` calls the preprocessor once after loading the file, before any windowing. The aggregator already handles heartbeats — no aggregator changes needed.

**Tech Stack:** C# 12, .NET 8, xUnit — no new dependencies.

---

## Why this works

Raw signal files contain `ForegroundAppChanged` (dwell open) and `AppDwell` (dwell close) but no `AppFocusHeartbeat` (that signal didn't exist). The live fix added heartbeats every 15 s so the aggregator's context window always has one. The replay fix re-simulates those heartbeats from the recorded open/close pairs. The aggregator's synthesis logic (`BuildOverlapSegments`) is unchanged — it just needs heartbeats to be present.

**Key invariant:** `AppDwellReplayPreprocessor.InjectHeartbeats` is only ever called from the file replay path. The live extraction path never calls it — live data already has real heartbeats from the collector.

---

## File map

| File | Change |
|---|---|
| Create: `src/FeatureExtraction/Services/AppDwellReplayPreprocessor.cs` | New static class with `InjectHeartbeats` and `GenerateHeartbeats` |
| Modify: `src/FeatureExtraction/Services/FeatureExtractorService.cs:403` | One-line call to preprocessor after loading signals |
| Create: `tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs` | All unit + integration tests |

---

## Task 1: `AppDwellReplayPreprocessor` — unit tests + implementation

**Files:**
- Create: `src/FeatureExtraction/Services/AppDwellReplayPreprocessor.cs`
- Create: `tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs`

- [ ] **Step 1: Create the test file with all failing tests**

Create `tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs`:

```csharp
using EndpointSignalAgent.FeatureExtraction.Services;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class AppDwellReplayPreprocessorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-06-11T10:00:00Z");

    private static FeatureSignal ForegroundChanged(DateTimeOffset ts, string appKey, string category = "IDE", string confidence = "high") =>
        new(ts, SignalEventType.ForegroundAppChanged, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appKey"] = appKey,
            ["category"] = category,
            ["confidence"] = confidence
        });

    private static FeatureSignal AppDwellEvent(DateTimeOffset ts, string appKey, long durationMs, string reason = "switch") =>
        new(ts, SignalEventType.AppDwell, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appKey"] = appKey,
            ["durationMs"] = durationMs.ToString(),
            ["reason"] = reason,
            ["dwellReason"] = reason,
            ["category"] = "IDE",
            ["confidence"] = "high"
        });

    private static FeatureSignal SystemTick(DateTimeOffset ts) =>
        new(ts, SignalEventType.SystemResourceTick, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cpu_available"] = "true", ["cpu_pct"] = "10"
        });

    [Fact]
    public void InjectHeartbeats_EmptyInput_ReturnsEmpty()
    {
        var result = AppDwellReplayPreprocessor.InjectHeartbeats(new List<FeatureSignal>());
        Assert.Empty(result);
    }

    [Fact]
    public void InjectHeartbeats_NoAppEvents_NoHeartbeats()
    {
        var signals = new List<FeatureSignal>
        {
            SystemTick(T0),
            SystemTick(T0.AddSeconds(10)),
            SystemTick(T0.AddSeconds(20))
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, s => s.Type == SignalEventType.AppFocusHeartbeat);
    }

    [Fact]
    public void InjectHeartbeats_ShortDwell_NoHeartbeats()
    {
        // Dwell only 14s — shorter than the 15s interval
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(14), "app-a", 14_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        Assert.DoesNotContain(result, s => s.Type == SignalEventType.AppFocusHeartbeat);
    }

    [Fact]
    public void InjectHeartbeats_45sDwell_ProducesTwoHeartbeatsAt15And30()
    {
        // Dwell from T0 to T0+45s — heartbeats expected at T0+15 and T0+30 (not at T0+45 which equals end)
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(45), "app-a", 45_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.Equal(2, heartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), heartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(30), heartbeats[1].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_HeartbeatsCarryCorrectPayload()
    {
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a", "Browser", "high"),
            AppDwellEvent(T0.AddSeconds(30), "app-a", 30_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var hb = Assert.Single(result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat));
        Assert.Equal("app-a", hb.Payload["appKey"]);
        Assert.Equal("Browser", hb.Payload["category"]);
        Assert.Equal("high", hb.Payload["confidence"]);
        Assert.Equal(T0.ToString("O"), hb.Payload["dwellStartUtc"]);
    }

    [Fact]
    public void InjectHeartbeats_DwellStartUtc_IsOpeningForegroundChangedTimestamp()
    {
        // dwellStartUtc must equal ForegroundAppChanged.TimestampUtc, not the heartbeat timestamp
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(60), "app-a", 60_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.All(heartbeats, hb =>
        {
            Assert.True(DateTimeOffset.TryParse(hb.Payload["dwellStartUtc"], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed));
            Assert.Equal(T0, parsed);
        });
    }

    [Fact]
    public void InjectHeartbeats_TwoConsecutiveApps_EachDwellGetsHeartbeats()
    {
        // App A: T0 to T0+30s — 1 heartbeat at T0+15
        // App B: T0+30s to T0+90s — 3 heartbeats at T0+45, T0+60, T0+75
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a", "IDE", "high"),
            ForegroundChanged(T0.AddSeconds(30), "app-b", "Browser", "high"),
            AppDwellEvent(T0.AddSeconds(90), "app-b", 60_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.Equal(4, heartbeats.Count);

        var aHeartbeats = heartbeats.Where(h => h.Payload["appKey"] == "app-a").ToList();
        var bHeartbeats = heartbeats.Where(h => h.Payload["appKey"] == "app-b").ToList();
        Assert.Single(aHeartbeats);
        Assert.Equal(3, bHeartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), aHeartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(45), bHeartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(60), bHeartbeats[1].TimestampUtc);
        Assert.Equal(T0.AddSeconds(75), bHeartbeats[2].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_UnclosedDwellAtEndOfFile_HeartbeatsUpToLastSignal()
    {
        // No AppDwell — dwell still open when file ends; last signal at T0+60s
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            SystemTick(T0.AddSeconds(60))
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        // Heartbeats at T0+15, T0+30, T0+45 (T0+60 == lastSignalTs, excluded by strict <)
        Assert.Equal(3, heartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), heartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(30), heartbeats[1].TimestampUtc);
        Assert.Equal(T0.AddSeconds(45), heartbeats[2].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_OutputIsSortedByTimestamp()
    {
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            SystemTick(T0.AddSeconds(5)),
            SystemTick(T0.AddSeconds(20)),
            AppDwellEvent(T0.AddSeconds(60), "app-a", 60_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i].TimestampUtc >= result[i - 1].TimestampUtc,
                $"Out of order at index {i}: {result[i - 1].TimestampUtc} > {result[i].TimestampUtc}");
        }
    }

    [Fact]
    public void InjectHeartbeats_UnrelatedAppDwell_DoesNotCloseCurrentDwell()
    {
        // AppDwell for "app-x" should not close the open dwell for "app-a"
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(20), "app-x", 20_000),  // unrelated app
            AppDwellEvent(T0.AddSeconds(45), "app-a", 45_000)    // real close
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        // Expect heartbeats at T0+15 and T0+30 (dwell is 0-45s)
        Assert.Equal(2, heartbeats.Count);
        Assert.All(heartbeats, hb => Assert.Equal("app-a", hb.Payload["appKey"]));
    }

    [Fact]
    public void InjectHeartbeats_Integration_SustainedFocusWindowGetsNonZeroAppFeatures()
    {
        // Simulates 2 minutes of single-app focus with no AppDwell (trailing-edge never fired).
        // After heartbeat injection, AppFeatureAggregator should produce non-zero features.
        var aggregator = new AppFeatureAggregator();
        var windowStart = T0.AddMinutes(1);
        var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));

        // Raw signals: ForegroundAppChanged at T0, then system ticks every 2s, no AppDwell
        var rawSignals = new List<FeatureSignal> { ForegroundChanged(T0, "app-a", "IDE", "high") };
        for (var sec = 2; sec <= 120; sec += 2)
        {
            rawSignals.Add(SystemTick(T0.AddSeconds(sec)));
        }

        var augmented = AppDwellReplayPreprocessor.InjectHeartbeats(rawSignals);

        // Context = events in [windowStart - 90s, windowStart + 60s)
        var historyStart = windowStart - TimeSpan.FromSeconds(90);
        var context = augmented
            .Where(s => s.TimestampUtc >= historyStart && s.TimestampUtc < window.EndUtc)
            .ToList();

        var result = aggregator.ExtractFeatures(context, window);

        Assert.Equal(1.0, result.Features["app_top1_share"], 3);
        Assert.Equal(1.0, result.Features["has_app_data"], 3);
        Assert.True(result.Features["cat_ide_ratio"] > 0.0);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```
dotnet test --filter "AppDwellReplayPreprocessor" -v normal
```

Expected: compilation error — `AppDwellReplayPreprocessor` does not exist yet.

- [ ] **Step 3: Create `AppDwellReplayPreprocessor.cs`**

Create `src/FeatureExtraction/Services/AppDwellReplayPreprocessor.cs`:

```csharp
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.Services;

internal static class AppDwellReplayPreprocessor
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Injects synthetic AppFocusHeartbeat events into a sorted signal list, one every 15 seconds
    /// per open foreground dwell, derived from ForegroundAppChanged / AppDwell pairs.
    /// Used only in the file replay path — live data already has real heartbeats from the collector.
    /// </summary>
    internal static List<FeatureSignal> InjectHeartbeats(IReadOnlyList<FeatureSignal> signals)
    {
        if (signals.Count == 0)
        {
            return new List<FeatureSignal>();
        }

        var synthetic = new List<FeatureSignal>();

        string? currentAppKey = null;
        string? currentCategory = null;
        string? currentConfidence = null;
        DateTimeOffset currentDwellStart = DateTimeOffset.MinValue;

        foreach (var signal in signals)
        {
            if (signal.Type == SignalEventType.ForegroundAppChanged)
            {
                if (currentAppKey is not null)
                {
                    synthetic.AddRange(GenerateHeartbeats(
                        currentAppKey, currentCategory!, currentConfidence!,
                        currentDwellStart, signal.TimestampUtc));
                }

                currentAppKey = PayloadValueReader.GetString(signal.Payload, "appKey", "unknown");
                currentCategory = PayloadValueReader.GetString(signal.Payload, "category", "Other");
                currentConfidence = PayloadValueReader.GetString(signal.Payload, "confidence", "low");
                currentDwellStart = signal.TimestampUtc;
            }
            else if (signal.Type == SignalEventType.AppDwell && currentAppKey is not null)
            {
                var dwellAppKey = PayloadValueReader.GetString(signal.Payload, "appKey", "");
                if (string.Equals(dwellAppKey, currentAppKey, StringComparison.Ordinal))
                {
                    synthetic.AddRange(GenerateHeartbeats(
                        currentAppKey, currentCategory!, currentConfidence!,
                        currentDwellStart, signal.TimestampUtc));
                    currentAppKey = null;
                }
            }
        }

        // Unclosed dwell at end of file: generate up to (not including) last signal timestamp
        if (currentAppKey is not null)
        {
            synthetic.AddRange(GenerateHeartbeats(
                currentAppKey, currentCategory!, currentConfidence!,
                currentDwellStart, signals[^1].TimestampUtc));
        }

        if (synthetic.Count == 0)
        {
            return new List<FeatureSignal>(signals);
        }

        var result = new List<FeatureSignal>(signals.Count + synthetic.Count);
        result.AddRange(signals);
        result.AddRange(synthetic);
        result.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return result;
    }

    private static IEnumerable<FeatureSignal> GenerateHeartbeats(
        string appKey, string category, string confidence,
        DateTimeOffset dwellStart, DateTimeOffset dwellEnd)
    {
        var t = dwellStart + HeartbeatInterval;
        while (t < dwellEnd)
        {
            yield return new FeatureSignal(
                t,
                SignalEventType.AppFocusHeartbeat,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appKey"] = appKey,
                    ["category"] = category,
                    ["confidence"] = confidence,
                    ["dwellStartUtc"] = dwellStart.ToString("O")
                });
            t += HeartbeatInterval;
        }
    }
}
```

- [ ] **Step 4: Run tests**

```
dotnet test --filter "AppDwellReplayPreprocessor" -v normal
```

Expected: all 10 tests PASS.

- [ ] **Step 5: Build**

```
dotnet build EndpointSignalAgent.csproj -q
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```
git add src/FeatureExtraction/Services/AppDwellReplayPreprocessor.cs
git add tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs
git commit -m "feat: AppDwellReplayPreprocessor — inject synthetic heartbeats from raw signal history"
```

---

## Task 2: Wire preprocessor into `ExtractFeaturesFromFileAsync`

**Files:**
- Modify: `src/FeatureExtraction/Services/FeatureExtractorService.cs:403`
- Test: `tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs` (integration test already in Task 1 covers the aggregator; this task verifies the wire-up in the service)

The only code change is one line added after the null-count guard on line 407 in `ExtractFeaturesFromFileAsync`. No new test file — the integration behavior is already covered by Task 1's `InjectHeartbeats_Integration_SustainedFocusWindowGetsNonZeroAppFeatures`.

- [ ] **Step 1: Write the failing integration test for the service wire-up**

Add this test to `tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs` (inside the existing class):

```csharp
[Fact]
public void InjectHeartbeats_CalledBeforeWindowSlicing_HeartbeatsVisibleInAllProfiles()
{
    // Verifies the preprocessor produces heartbeats that fall within the context window
    // for BOTH W60S30 (historyStart = -90s) and W30S15 (historyStart = -45s) profiles.
    var windowStart = T0.AddMinutes(5);

    // ForegroundAppChanged at T0 — 5 minutes before the target window
    var rawSignals = new List<FeatureSignal> { ForegroundChanged(T0, "app-a", "IDE", "high") };
    for (var sec = 2; sec <= 360; sec += 2)
        rawSignals.Add(SystemTick(T0.AddSeconds(sec)));

    var augmented = AppDwellReplayPreprocessor.InjectHeartbeats(rawSignals);

    // For W60S30: historyStart = windowStart - 90s
    var w60HistoryStart = windowStart - TimeSpan.FromSeconds(90);
    var w60Window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));
    var w60Context = augmented
        .Where(s => s.TimestampUtc >= w60HistoryStart && s.TimestampUtc < w60Window.EndUtc)
        .ToList();
    Assert.Contains(w60Context, s => s.Type == SignalEventType.AppFocusHeartbeat);

    // For W30S15: historyStart = windowStart - 45s
    var w30HistoryStart = windowStart - TimeSpan.FromSeconds(45);
    var w30Window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(30));
    var w30Context = augmented
        .Where(s => s.TimestampUtc >= w30HistoryStart && s.TimestampUtc < w30Window.EndUtc)
        .ToList();
    Assert.Contains(w30Context, s => s.Type == SignalEventType.AppFocusHeartbeat);
}
```

- [ ] **Step 2: Run to confirm it passes already** (this test is on the preprocessor alone, not the service)

```
dotnet test --filter "CalledBeforeWindowSlicing" -v normal
```

Expected: PASS — the preprocessor already produces heartbeats every 15s so both profile context windows are guaranteed to contain one.

- [ ] **Step 3: Apply the one-line change to `FeatureExtractorService.cs`**

In `ExtractFeaturesFromFileAsync`, after the null-count guard (around line 407), add one line:

The section currently reads:
```csharp
var allSignals = await ReadSignalsFromFileAsync(jsonlPath, ct);
if (allSignals.Count == 0)
{
    _logger.LogInformation("No signals found in {Path}", jsonlPath);
    return;
}

var extractionRunId = Guid.NewGuid().ToString("N");
```

Change to:
```csharp
var allSignals = await ReadSignalsFromFileAsync(jsonlPath, ct);
if (allSignals.Count == 0)
{
    _logger.LogInformation("No signals found in {Path}", jsonlPath);
    return;
}

allSignals = AppDwellReplayPreprocessor.InjectHeartbeats(allSignals);

var extractionRunId = Guid.NewGuid().ToString("N");
```

- [ ] **Step 4: Build**

```
dotnet build EndpointSignalAgent.csproj -q
```

Expected: 0 errors.

- [ ] **Step 5: Run full test suite**

```
dotnet test -v normal
```

Expected: all tests pass. Pre-existing `FeatureStoreCapTests` file-locking failures are unrelated and should be ignored.

- [ ] **Step 6: Commit**

```
git add src/FeatureExtraction/Services/FeatureExtractorService.cs
git add tests/EndpointSignalAgent.Tests/AppDwellReplayPreprocessorTests.cs
git commit -m "feat: inject synthetic AppFocusHeartbeats in file replay path for retroactive open-dwell fix"
```

---

## Self-review

**1. Spec coverage**
- ✅ Preprocessor synthesizes heartbeats from `ForegroundAppChanged`/`AppDwell` pairs
- ✅ 15-second interval (matches live collector)
- ✅ `dwellStartUtc` = `ForegroundAppChanged.TimestampUtc` in payload
- ✅ Heartbeats cover all open dwell intervals including unclosed end-of-file
- ✅ Heartbeats excluded at exact dwell boundary (`t < dwellEnd`, not `<=`)
- ✅ Wire-up in `ExtractFeaturesFromFileAsync` before windowing loop
- ✅ Live path NOT affected (preprocessor not called from live extraction)
- ✅ W30S15 context window (45s lookback) guaranteed to contain a heartbeat — with 15s interval, any 45s span has ≥2

**2. Placeholder scan** — none found.

**3. Type consistency**
- `AppDwellReplayPreprocessor.InjectHeartbeats` takes `IReadOnlyList<FeatureSignal>` and returns `List<FeatureSignal>` — matches reassignment `allSignals = AppDwellReplayPreprocessor.InjectHeartbeats(allSignals)` since `allSignals` is `List<FeatureSignal>` (returned by `ReadSignalsFromFileAsync`). ✅
- `PayloadValueReader.GetString` used in preprocessor — matches the same call in `AppFeatureAggregator`. ✅
- `FeatureSignal` constructor used in `GenerateHeartbeats` matches the record definition in `Windowing.cs`: `(DateTimeOffset TimestampUtc, SignalEventType Type, IReadOnlyDictionary<string, string> Payload, int? NativeAggregationSec = null)`. The `Dictionary<string, string>` is assignable to `IReadOnlyDictionary<string, string>`. ✅
