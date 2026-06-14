# Feature Schema v1.3 — Temporal Context Add-on: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compute 18 temporal features (schema v1.3) per window by replaying `raw_signals.jsonl` with h300/h600 horizons clipped to collection-session boundaries.

**Architecture:** A `TemporalAggregator` layer groups features into five pure-function aggregators (metadata, app, session, network, system+cross). Each aggregator receives a `TemporalWindowContext` containing pre-sliced `FeatureSignal` lists for h300/h600 and full session history for state reconstruction. A `TemporalExtractionRunner` reads `raw_signals.jsonl` via `RawSignalReplayReader`, enumerates windows for each supported profile, builds context objects, and calls `TemporalFeatureExtractor` to produce one `Dictionary<string, double>` per window.

**Tech Stack:** C# (.NET 8), xUnit, `System.Text.Json`, existing `FeatureSignal` / `PayloadValueReader` / `SlidingWindowing` / `FeatureMath` utilities in `EndpointSignalAgent.FeatureExtraction.SignalAggregator`, `SessionFeatureAggregator` + `CrossFeatureAggregator` reused for session-state reconstruction.

---

## File Map

| Status | Path | Responsibility |
|--------|------|----------------|
| Create | `src/FeatureExtraction/TemporalAggregator/TemporalWindowContext.cs` | Input record for all temporal aggregators |
| Create | `src/FeatureExtraction/TemporalAggregator/ITemporalAggregator.cs` | Interface: `Aggregate(ctx)` |
| Create | `src/FeatureExtraction/TemporalAggregator/TemporalMetadataAggregator.cs` | 5 metadata/quality features |
| Create | `src/FeatureExtraction/TemporalAggregator/AppTemporalAggregator.cs` | 4 app temporal features |
| Create | `src/FeatureExtraction/TemporalAggregator/SessionTemporalAggregator.cs` | 3 session temporal features |
| Create | `src/FeatureExtraction/TemporalAggregator/NetworkTemporalAggregator.cs` | 3 network temporal features |
| Create | `src/FeatureExtraction/TemporalAggregator/SystemTemporalAggregator.cs` | 2 system temporal features |
| Create | `src/FeatureExtraction/TemporalAggregator/CrossTemporalAggregator.cs` | 1 cross temporal feature |
| Create | `src/FeatureExtraction/TemporalAggregator/TemporalFeatureExtractor.cs` | Orchestrator: wires all aggregators |
| Create | `src/FeatureExtraction/ReplayPipeline/RawSignalReplayReader.cs` | Reads + deserialises `raw_signals.jsonl` |
| Create | `src/FeatureExtraction/ReplayPipeline/TemporalExtractionRunner.cs` | Enumerates windows, builds context, calls extractor |
| Modify | `src/FeatureExtraction/SignalAggregator/FeatureSchema.cs` | Add `FeatureVersion13 = "1.3"` + `TemporalColumns[]` |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalWindowContextTests.cs` | |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalMetadataAggregatorTests.cs` | |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/AppTemporalAggregatorTests.cs` | |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/SessionTemporalAggregatorTests.cs` | |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/NetworkTemporalAggregatorTests.cs` | |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/SystemCrossTemporalAggregatorTests.cs` | |
| Create | `tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalExtractionRunnerTests.cs` | End-to-end replay smoke test |

---

## Shared test helpers (copy into each test file that needs them)

```csharp
// Add at the bottom of each test class — these mirror the pattern from WindowedFeatureExtractionTests.cs
private static DateTimeOffset T(string s) =>
    DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

private static FeatureSignal E(
    string ts,
    SignalEventType type,
    Dictionary<string, string>? payload = null) =>
    new(T(ts), type, (IReadOnlyDictionary<string, string>)(payload ?? new Dictionary<string, string>()));
```

---

## Task 1: `TemporalWindowContext` + `ITemporalAggregator`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/TemporalWindowContext.cs`
- Create: `src/FeatureExtraction/TemporalAggregator/ITemporalAggregator.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalWindowContextTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalWindowContextTests.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class TemporalWindowContextTests
{
    [Fact]
    public void H300ValidSec_ClipsToSessionStart_WhenSessionYoungerThan300s()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(120); // session only 120 s old

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        Assert.Equal(120.0, ctx.H300ValidSec, precision: 1);
        Assert.Equal(120.0, ctx.H600ValidSec, precision: 1);
    }

    [Fact]
    public void H300ValidSec_Returns300_WhenSessionOlderThan300s()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(400);

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        Assert.Equal(300.0, ctx.H300ValidSec, precision: 1);
        Assert.Equal(400.0, ctx.H600ValidSec, precision: 1); // session only 400 s, h600 clips to 400
    }

    [Fact]
    public void Build_SlicesSignalsIntoCorrectHorizons()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = T("2026-06-01T11:00:00Z");
        // h300 = [10:55:00, 11:00:00); h600 = [10:50:00, 11:00:00)

        var signals = new[]
        {
            E("2026-06-01T10:56:00Z", SignalEventType.ForegroundAppChanged), // in h300
            E("2026-06-01T10:52:00Z", SignalEventType.ForegroundAppChanged), // h600 only
            E("2026-06-01T10:49:00Z", SignalEventType.ForegroundAppChanged), // outside both, in session
        };

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        Assert.Single(ctx.H300Signals);
        Assert.Equal(2, ctx.H600Signals.Count);
        Assert.Equal(3, ctx.SessionSignals.Count);
    }

    [Fact]
    public void Build_ExcludesEventsAtOrAfterWindowEnd()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = T("2026-06-01T11:00:00Z");

        var signals = new[]
        {
            E("2026-06-01T11:00:00Z", SignalEventType.ForegroundAppChanged), // at WindowEnd — excluded
            E("2026-06-01T10:59:59Z", SignalEventType.ForegroundAppChanged), // just before — included
        };

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        Assert.Single(ctx.H300Signals);
    }

    // --- helpers ---
    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(string ts, SignalEventType type, Dictionary<string, string>? p = null) =>
        new(T(ts), type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
```

- [ ] **Step 2: Run tests — expect compile failure (`TemporalWindowContext` not found)**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "TemporalWindowContextTests" -v n
```

Expected: build error.

- [ ] **Step 3: Create `ITemporalAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/ITemporalAggregator.cs
namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal interface ITemporalAggregator
{
    IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx);
}
```

- [ ] **Step 4: Create `TemporalWindowContext`**

```csharp
// src/FeatureExtraction/TemporalAggregator/TemporalWindowContext.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed record TemporalWindowContext
{
    public required DateTimeOffset WindowEnd    { get; init; }
    public required DateTimeOffset SessionStart { get; init; }

    // [max(SessionStart, WindowEnd-300s), WindowEnd), ordered ascending
    public required IReadOnlyList<FeatureSignal> H300Signals    { get; init; }
    // [max(SessionStart, WindowEnd-600s), WindowEnd), ordered ascending
    public required IReadOnlyList<FeatureSignal> H600Signals    { get; init; }
    // [SessionStart, WindowEnd), ordered ascending — for state reconstruction
    public required IReadOnlyList<FeatureSignal> SessionSignals { get; init; }

    public double H300ValidSec
    {
        get
        {
            var h300Start = WindowEnd.AddSeconds(-300);
            var effective = h300Start > SessionStart ? h300Start : SessionStart;
            return Math.Max(0.0, (WindowEnd - effective).TotalSeconds);
        }
    }

    public double H600ValidSec
    {
        get
        {
            var h600Start = WindowEnd.AddSeconds(-600);
            var effective = h600Start > SessionStart ? h600Start : SessionStart;
            return Math.Max(0.0, (WindowEnd - effective).TotalSeconds);
        }
    }

    public static TemporalWindowContext Build(
        DateTimeOffset windowEnd,
        DateTimeOffset sessionStart,
        IReadOnlyList<FeatureSignal> allSessionSignals)
    {
        var h300Effective = Max(windowEnd.AddSeconds(-300), sessionStart);
        var h600Effective = Max(windowEnd.AddSeconds(-600), sessionStart);

        return new TemporalWindowContext
        {
            WindowEnd    = windowEnd,
            SessionStart = sessionStart,
            H300Signals  = allSessionSignals
                .Where(s => s.TimestampUtc >= h300Effective && s.TimestampUtc < windowEnd)
                .ToList(),
            H600Signals  = allSessionSignals
                .Where(s => s.TimestampUtc >= h600Effective && s.TimestampUtc < windowEnd)
                .ToList(),
            SessionSignals = allSessionSignals
                .Where(s => s.TimestampUtc >= sessionStart && s.TimestampUtc < windowEnd)
                .ToList()
        };
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
```

- [ ] **Step 5: Run tests — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "TemporalWindowContextTests" -v n
```

Expected: all 4 tests PASS.

- [ ] **Step 6: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/TemporalWindowContext.cs
git add src/FeatureExtraction/TemporalAggregator/ITemporalAggregator.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalWindowContextTests.cs
git commit -m "feat: add TemporalWindowContext + ITemporalAggregator contracts for v1.3 schema"
```

---

## Task 2: `TemporalMetadataAggregator`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/TemporalMetadataAggregator.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalMetadataAggregatorTests.cs`

Features produced: `session_age_sec`, `history_coverage_ratio_h300`, `history_coverage_ratio_h600`, `is_temporal_warmup_window`, `raw_event_count_h300`.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalMetadataAggregatorTests.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class TemporalMetadataAggregatorTests
{
    private readonly TemporalMetadataAggregator _agg = new();

    [Fact]
    public void SessionAgeSec_IsWindowEndMinusSessionStart()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(450);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(450.0, features["session_age_sec"], precision: 1);
    }

    [Fact]
    public void HistoryCoverageRatio_ClipsAtOne_WhenSessionOlderThanHorizon()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(1.0, features["history_coverage_ratio_h300"], precision: 3);
        Assert.Equal(1.0, features["history_coverage_ratio_h600"], precision: 3);
        Assert.Equal(0.0, features["is_temporal_warmup_window"]);
    }

    [Fact]
    public void IsTemporalWarmupWindow_IsOne_WhenCoverageLessThanFull()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(200); // < 300 s
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(1.0, features["is_temporal_warmup_window"]);
    }

    [Fact]
    public void RawEventCountH300_CountsOnlyH300Signals()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = T("2026-06-01T11:00:00Z");
        // h300 = [10:55:00, 11:00:00)
        var signals = new[]
        {
            E("2026-06-01T10:56:00Z", SignalEventType.ForegroundAppChanged), // in h300
            E("2026-06-01T10:52:00Z", SignalEventType.ForegroundAppChanged), // outside h300
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(1.0, features["raw_event_count_h300"]);
    }

    private static Dictionary<string, double> ToDictionary(IEnumerable<KeyValuePair<string, double>> src)
        => src.ToDictionary(kv => kv.Key, kv => kv.Value);

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(string ts, SignalEventType type, Dictionary<string, string>? p = null) =>
        new(T(ts), type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
```

- [ ] **Step 2: Run — expect compile failure**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "TemporalMetadataAggregatorTests" -v n
```

- [ ] **Step 3: Implement `TemporalMetadataAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/TemporalMetadataAggregator.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class TemporalMetadataAggregator : ITemporalAggregator
{
    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        yield return Kv("session_age_sec", (ctx.WindowEnd - ctx.SessionStart).TotalSeconds);

        var coverH300 = Math.Min(1.0, FeatureMath.SafeDivide(ctx.H300ValidSec, 300.0));
        yield return Kv("history_coverage_ratio_h300", coverH300);

        yield return Kv("history_coverage_ratio_h600",
            Math.Min(1.0, FeatureMath.SafeDivide(ctx.H600ValidSec, 600.0)));

        yield return Kv("is_temporal_warmup_window", coverH300 < 1.0 ? 1.0 : 0.0);

        yield return Kv("raw_event_count_h300", ctx.H300Signals.Count);
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
```

- [ ] **Step 4: Run — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "TemporalMetadataAggregatorTests" -v n
```

Expected: all 4 tests PASS.

- [ ] **Step 5: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/TemporalMetadataAggregator.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalMetadataAggregatorTests.cs
git commit -m "feat: add TemporalMetadataAggregator (5 metadata features)"
```

---

## Task 3: `AppTemporalAggregator`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/AppTemporalAggregator.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/AppTemporalAggregatorTests.cs`

Features: `category_transition_count_h300`, `category_transition_entropy_h300`, `app_switch_interval_std_ms_h300`, `app_dwell_cv_h300`.

Key rules:
- "Committed category change" = consecutive `ForegroundAppChanged` events with different `payload["category"]` values.
- Entropy uses `FeatureMath.EntropyFromShares` (natural log, already in the codebase).
- `FeatureMath.StdDev` uses population std (N denominator) — matches existing usage.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/AppTemporalAggregatorTests.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class AppTemporalAggregatorTests
{
    private readonly AppTemporalAggregator _agg = new();

    private static TemporalWindowContext Ctx(
        IReadOnlyList<FeatureSignal> signals,
        int sessionAgeSec = 700)
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(sessionAgeSec);
        return TemporalWindowContext.Build(windowEnd, sessionStart, signals);
    }

    [Fact]
    public void CategoryTransitionCount_CountsDistinctCategoryChanges()
    {
        // browser→ide→ide→terminal = 2 transitions (browser→ide, ide→terminal)
        var signals = H300Signals(new[]
        {
            ("browser",  0),
            ("ide",     10),
            ("ide",     20),   // same category — not a transition
            ("terminal",30),
        });
        var features = Run(Ctx(signals));
        Assert.Equal(2.0, features["category_transition_count_h300"]);
    }

    [Fact]
    public void CategoryTransitionEntropy_IsZero_WhenSingleTransitionPair()
    {
        // A→B, A→B, A→B — only one distinct pair → entropy = 0
        var signals = H300Signals(new[]
        {
            ("A", 0), ("B", 5), ("A", 10), ("B", 15), ("A", 20), ("B", 25),
        });
        var features = Run(Ctx(signals));
        Assert.Equal(0.0, features["category_transition_entropy_h300"], precision: 3);
    }

    [Fact]
    public void CategoryTransitionEntropy_IsPositive_WhenMultiplePairs()
    {
        var signals = H300Signals(new[]
        {
            ("browser",  0),
            ("ide",     10),   // browser→ide
            ("terminal",20),   // ide→terminal
        });
        var features = Run(Ctx(signals));
        Assert.True(features["category_transition_entropy_h300"] > 0.0);
    }

    [Fact]
    public void AppSwitchIntervalStd_IsZero_WhenFewerThanTwoSwitches()
    {
        var signals = H300Signals(new[] { ("browser", 0) });
        var features = Run(Ctx(signals));
        Assert.Equal(0.0, features["app_switch_interval_std_ms_h300"]);
    }

    [Fact]
    public void AppSwitchIntervalStd_IsZero_WhenAllIntervalsSame()
    {
        // 3 switches each 10 s apart → intervals = [10000, 10000] → std = 0
        var signals = H300Signals(new[]
        {
            ("browser",   0),
            ("ide",      10),
            ("terminal", 20),
        });
        var features = Run(Ctx(signals));
        Assert.Equal(0.0, features["app_switch_interval_std_ms_h300"], precision: 1);
    }

    [Fact]
    public void AppDwellCv_IsZero_WhenNoDwellEvents()
    {
        var ctx = TemporalWindowContext.Build(
            T("2026-06-01T10:11:40Z"),
            T("2026-06-01T10:00:00Z"),
            Array.Empty<FeatureSignal>());
        var features = Run(ctx);
        Assert.Equal(0.0, features["app_dwell_cv_h300"]);
    }

    [Fact]
    public void AppDwellCv_IsRatioOfStdToMean()
    {
        // dwells: 1000 ms and 3000 ms → mean=2000, std(pop)=1000, CV=0.5
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var h300Start    = windowEnd.AddSeconds(-300);
        var signals = new[]
        {
            new FeatureSignal(h300Start.AddSeconds(10), SignalEventType.AppDwell,
                new Dictionary<string, string> { ["durationMs"] = "1000" }),
            new FeatureSignal(h300Start.AddSeconds(20), SignalEventType.AppDwell,
                new Dictionary<string, string> { ["durationMs"] = "3000" }),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);
        Assert.Equal(0.5, features["app_dwell_cv_h300"], precision: 3);
    }

    private static Dictionary<string, double> Run(TemporalWindowContext ctx)
    {
        var agg = new AppTemporalAggregator();
        return agg.Aggregate(ctx).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // Builds ForegroundAppChanged signals in h300 (sessionAge 700 s → h300 starts at 400 s)
    private static IReadOnlyList<FeatureSignal> H300Signals(IEnumerable<(string cat, int offsetSec)> items)
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var h300Start    = sessionStart.AddSeconds(700 - 300);
        return items.Select(i =>
            new FeatureSignal(
                h300Start.AddSeconds(i.offsetSec),
                SignalEventType.ForegroundAppChanged,
                new Dictionary<string, string> { ["category"] = i.cat }))
            .ToList<FeatureSignal>();
    }

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
```

- [ ] **Step 2: Run — expect compile failure**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "AppTemporalAggregatorTests" -v n
```

- [ ] **Step 3: Implement `AppTemporalAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/AppTemporalAggregator.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class AppTemporalAggregator : ITemporalAggregator
{
    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        var switches = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.ForegroundAppChanged)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        // Build committed category-transition pairs
        var transitionPairs = new List<string>();
        string? prevCat = null;
        foreach (var ev in switches)
        {
            var cat = PayloadValueReader.GetString(ev.Payload, "category", "other");
            if (prevCat is not null && cat != prevCat)
                transitionPairs.Add($"{prevCat}→{cat}");
            prevCat = cat;
        }

        yield return Kv("category_transition_count_h300", transitionPairs.Count);

        double entropy = 0.0;
        if (transitionPairs.Count > 0)
        {
            var total = (double)transitionPairs.Count;
            var shares = transitionPairs
                .GroupBy(p => p)
                .Select(g => g.Count() / total);
            entropy = FeatureMath.EntropyFromShares(shares);
        }
        yield return Kv("category_transition_entropy_h300", entropy);

        double switchStd = 0.0;
        if (switches.Count >= 2)
        {
            var intervals = switches
                .Zip(switches.Skip(1), (a, b) => (b.TimestampUtc - a.TimestampUtc).TotalMilliseconds)
                .ToList();
            switchStd = FeatureMath.StdDev(intervals);
        }
        yield return Kv("app_switch_interval_std_ms_h300", switchStd);

        var dwells = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.AppDwell)
            .Select(e => PayloadValueReader.GetDouble(e.Payload, "durationMs"))
            .Where(ms => ms > 0)
            .ToList();

        double cv = 0.0;
        if (dwells.Count >= 2)
        {
            var mean = dwells.Average();
            if (mean > 0)
                cv = FeatureMath.StdDev(dwells) / mean;
        }
        yield return Kv("app_dwell_cv_h300", cv);
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
```

- [ ] **Step 4: Run — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "AppTemporalAggregatorTests" -v n
```

Expected: all 7 tests PASS.

- [ ] **Step 5: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/AppTemporalAggregator.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/AppTemporalAggregatorTests.cs
git commit -m "feat: add AppTemporalAggregator (4 app temporal features)"
```

---

## Task 4: `SessionTemporalAggregator`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/SessionTemporalAggregator.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/SessionTemporalAggregatorTests.cs`

Features: `active_work_ratio_h300`, `active_work_streak_sec`, `time_since_unlock_sec`.

Key rules:
- Reuses `SessionFeatureAggregator` with `[SessionStart, WindowEnd)` window for full interval list; then slices for h300.
- Active-work rule: `!locked AND displayOn AND !presenceAway AND idleBucketSec < 300` — identical to `CrossFeatureAggregator.BuildActiveWorkIntervals`.
- `active_work_streak_sec`: walks intervals in reverse from `WindowEnd`; stops at the first non-active interval.
- `time_since_unlock_sec`: last `SessionUnlock` in `SessionSignals`, capped at 600; returns 600 when no unlock seen.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/SessionTemporalAggregatorTests.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class SessionTemporalAggregatorTests
{
    [Fact]
    public void ActiveWorkRatioH300_IsOne_WhenDisplayOnAndUnlocked()
    {
        // Session started 700 s ago. h300 window = last 300 s. Display on, never locked.
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1),  SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(2),  SignalEventType.SessionUnlock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(1.0, features["active_work_ratio_h300"], precision: 3);
    }

    [Fact]
    public void ActiveWorkRatioH300_IsZero_WhenLockedEntireH300()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1), SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(2), SignalEventType.SessionLock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(0.0, features["active_work_ratio_h300"], precision: 3);
    }

    [Fact]
    public void ActiveWorkStreak_IsZero_WhenCurrentlyLocked()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1),   SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(500), SignalEventType.SessionLock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(0.0, features["active_work_streak_sec"]);
    }

    [Fact]
    public void ActiveWorkStreak_ReflectsDurationSinceLockEnd()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        // Lock from t=200 to t=500, then unlocked and display on until window_end
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1),   SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(200), SignalEventType.SessionLock),
            E(sessionStart.AddSeconds(500), SignalEventType.SessionUnlock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(200.0, features["active_work_streak_sec"], precision: 1); // 700-500=200 s active
    }

    [Fact]
    public void TimeSinceUnlock_IsCappedAt600()
    {
        // Last unlock was 800 s before window_end → capped at 600
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(1000);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(200), SignalEventType.SessionUnlock), // 800 s before windowEnd
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_unlock_sec"]);
    }

    [Fact]
    public void TimeSinceUnlock_Is600_WhenNoUnlockObserved()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_unlock_sec"]);
    }

    private static Dictionary<string, double> Run(TemporalWindowContext ctx) =>
        new SessionTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(DateTimeOffset ts, SignalEventType type,
        Dictionary<string, string>? p = null) =>
        new(ts, type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
```

- [ ] **Step 2: Run — expect compile failure**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "SessionTemporalAggregatorTests" -v n
```

- [ ] **Step 3: Implement `SessionTemporalAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/SessionTemporalAggregator.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class SessionTemporalAggregator : ITemporalAggregator
{
    private readonly SessionFeatureAggregator _sessionAgg = new();

    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        // Build full [SessionStart, WindowEnd) intervals for streak + active ratio
        var fullWindow    = new SlidingWindow(ctx.SessionStart, ctx.WindowEnd);
        var sessionResult = _sessionAgg.ExtractFeatures(ctx.SessionSignals, fullWindow);
        var intervals     = sessionResult.Intervals;
        var activeList    = CrossFeatureAggregator.BuildActiveWorkIntervals(intervals);

        // active_work_ratio_h300: active ms in h300 / h300 valid ms
        var h300Effective = ctx.WindowEnd.AddSeconds(-300) > ctx.SessionStart
            ? ctx.WindowEnd.AddSeconds(-300)
            : ctx.SessionStart;
        var h300ValidMs = (long)(ctx.WindowEnd - h300Effective).TotalMilliseconds;
        var activeH300Ms = 0L;
        foreach (var a in activeList)
            activeH300Ms += SlidingWindowing.OverlapMs(a.StartUtc, a.EndUtc, h300Effective, ctx.WindowEnd);
        yield return Kv("active_work_ratio_h300", FeatureMath.SafeDivide(activeH300Ms, h300ValidMs));

        // active_work_streak_sec: walk intervals in reverse from WindowEnd
        double streakSec = 0.0;
        foreach (var interval in intervals.Reverse())
        {
            var displayOn  = interval.DisplayState == SessionDisplayState.On;
            var presenceOk = !interval.PresenceKnown || !interval.PresenceAway;
            var idleOk     = !interval.IdleKnown || interval.IdleBucketSec < 300;
            var active     = !interval.Locked && displayOn && presenceOk && idleOk;
            if (!active) break;
            streakSec += (interval.EndUtc - interval.StartUtc).TotalSeconds;
        }
        yield return Kv("active_work_streak_sec", streakSec);

        // time_since_unlock_sec: latest SessionUnlock in session, capped at 600
        var latestUnlock = ctx.SessionSignals
            .Where(e => e.Type == SignalEventType.SessionUnlock)
            .Select(e => e.TimestampUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        var timeSinceUnlock = latestUnlock == DateTimeOffset.MinValue
            ? 600.0
            : Math.Min(600.0, (ctx.WindowEnd - latestUnlock).TotalSeconds);
        yield return Kv("time_since_unlock_sec", timeSinceUnlock);
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
```

- [ ] **Step 4: Run — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "SessionTemporalAggregatorTests" -v n
```

Expected: all 6 tests PASS.

- [ ] **Step 5: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/SessionTemporalAggregator.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/SessionTemporalAggregatorTests.cs
git commit -m "feat: add SessionTemporalAggregator (active_work_ratio, streak, time_since_unlock)"
```

---

## Task 5: `NetworkTemporalAggregator`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/NetworkTemporalAggregator.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/NetworkTemporalAggregatorTests.cs`

Features: `network_change_count_h300`, `time_since_network_change_sec`, `network_context_stability_ratio_h600`.

Key rules:
- Initial snapshots carry `payload["initial"] = "true"` (set by `NetworkContextCollector.EmitInitialStateAsync`). Exclude them from all three features.
- `network_context_stability_ratio_h600` = time from last non-initial change to `WindowEnd` / `H600ValidSec`. Returns 1.0 when no changes exist in h600.
- Both `time_since_*` features cap at 600 s; return 600 when no relevant event found in `SessionSignals`.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/NetworkTemporalAggregatorTests.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class NetworkTemporalAggregatorTests
{
    [Fact]
    public void NetworkChangeCountH300_ExcludesInitialSnapshots()
    {
        var (sessionStart, windowEnd) = Session(700);
        var h300Start = windowEnd.AddSeconds(-300);

        var signals = new[]
        {
            E(h300Start.AddSeconds(10), SignalEventType.VpnStateChanged,
                new() { ["initial"] = "true" }),           // excluded
            E(h300Start.AddSeconds(20), SignalEventType.VpnStateChanged,
                new() { ["vpnOn"] = "true" }),             // counted
            E(h300Start.AddSeconds(30), SignalEventType.WifiLinkChanged,
                new() { ["wifiUp"] = "false" }),           // counted
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(2.0, features["network_change_count_h300"]);
    }

    [Fact]
    public void NetworkChangeCountH300_IsZero_WhenOnlyInitialSignals()
    {
        var (sessionStart, windowEnd) = Session(700);
        var h300Start = windowEnd.AddSeconds(-300);

        var signals = new[]
        {
            E(h300Start.AddSeconds(5), SignalEventType.VpnStateChanged, new() { ["initial"] = "true" }),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(0.0, features["network_change_count_h300"]);
    }

    [Fact]
    public void TimeSinceNetworkChange_IsCappedAt600()
    {
        var (sessionStart, windowEnd) = Session(1000);
        // Last change 800 s before window_end → capped at 600
        var signals = new[]
        {
            E(windowEnd.AddSeconds(-800), SignalEventType.VpnStateChanged, new() { ["vpnOn"] = "true" }),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_network_change_sec"]);
    }

    [Fact]
    public void TimeSinceNetworkChange_Is600_WhenNoChangeEverObserved()
    {
        var (sessionStart, windowEnd) = Session(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());
        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_network_change_sec"]);
    }

    [Fact]
    public void NetworkContextStabilityRatioH600_IsOne_WhenNoChangesInH600()
    {
        var (sessionStart, windowEnd) = Session(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());
        var features = Run(ctx);

        Assert.Equal(1.0, features["network_context_stability_ratio_h600"], precision: 3);
    }

    [Fact]
    public void NetworkContextStabilityRatioH600_EqualsStableTimeFraction()
    {
        // Session 700 s old. h600 valid = 600 s. Change at WindowEnd-200 s.
        // Stable since: 200 s → ratio = 200/600 ≈ 0.333
        var (sessionStart, windowEnd) = Session(700);
        var signals = new[]
        {
            E(windowEnd.AddSeconds(-200), SignalEventType.LocalNetworkChanged),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(200.0 / 600.0, features["network_context_stability_ratio_h600"], precision: 3);
    }

    private static Dictionary<string, double> Run(TemporalWindowContext ctx) =>
        new NetworkTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static (DateTimeOffset sessionStart, DateTimeOffset windowEnd) Session(int ageSec)
    {
        var s = T("2026-06-01T10:00:00Z");
        return (s, s.AddSeconds(ageSec));
    }

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(DateTimeOffset ts, SignalEventType type,
        Dictionary<string, string>? p = null) =>
        new(ts, type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
```

- [ ] **Step 2: Run — expect compile failure**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "NetworkTemporalAggregatorTests" -v n
```

- [ ] **Step 3: Implement `NetworkTemporalAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/NetworkTemporalAggregator.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class NetworkTemporalAggregator : ITemporalAggregator
{
    private static readonly HashSet<SignalEventType> NetworkTypes = new()
    {
        SignalEventType.VpnStateChanged,
        SignalEventType.WifiLinkChanged,
        SignalEventType.WifiSsidChanged,
        SignalEventType.LocalNetworkChanged,
        SignalEventType.PublicIpBucketChanged
    };

    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        yield return Kv("network_change_count_h300",
            ctx.H300Signals.Count(IsNonInitial));

        var latestChange = ctx.SessionSignals
            .Where(IsNonInitial)
            .Select(e => e.TimestampUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        yield return Kv("time_since_network_change_sec",
            latestChange == DateTimeOffset.MinValue
                ? 600.0
                : Math.Min(600.0, (ctx.WindowEnd - latestChange).TotalSeconds));

        var h600Changes = ctx.H600Signals.Where(IsNonInitial).OrderBy(e => e.TimestampUtc).ToList();
        if (h600Changes.Count == 0)
        {
            yield return Kv("network_context_stability_ratio_h600", 1.0);
        }
        else
        {
            var stableSec = (ctx.WindowEnd - h600Changes.Last().TimestampUtc).TotalSeconds;
            yield return Kv("network_context_stability_ratio_h600",
                FeatureMath.SafeDivide(stableSec, ctx.H600ValidSec));
        }
    }

    private static bool IsNonInitial(FeatureSignal e) =>
        NetworkTypes.Contains(e.Type) &&
        !PayloadValueReader.IsTruthy(PayloadValueReader.GetString(e.Payload, "initial"));

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
```

- [ ] **Step 4: Run — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "NetworkTemporalAggregatorTests" -v n
```

Expected: all 6 tests PASS.

- [ ] **Step 5: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/NetworkTemporalAggregator.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/NetworkTemporalAggregatorTests.cs
git commit -m "feat: add NetworkTemporalAggregator (3 network temporal features)"
```

---

## Task 6: `SystemTemporalAggregator` + `CrossTemporalAggregator`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/SystemTemporalAggregator.cs`
- Create: `src/FeatureExtraction/TemporalAggregator/CrossTemporalAggregator.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/SystemCrossTemporalAggregatorTests.cs`

Features:
- `cpu_high_persistence_h300` — ratio of `SystemResourceTick` samples in h300 where `cpu_pct >= 80`.
- `net_throughput_trend_h300` — OLS slope of `net_rx_kbps + net_tx_kbps` over sample index in h300; returns 0 when < 2 ticks.
- `active_resource_mismatch_h300` — fraction of h300 `SystemResourceTick` samples where (`cpu_pct >= 80` OR `mem_used_pct >= 80`) AND session is NOT in active_work state. Returns 0 when no ticks.

Payload keys for `SystemResourceTick`: `cpu_pct`, `mem_used_pct`, `net_rx_kbps`, `net_tx_kbps` (same keys used by `SystemResourceFeatureAggregator`).

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/SystemCrossTemporalAggregatorTests.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class SystemCrossTemporalAggregatorTests
{
    // --- SystemTemporalAggregator ---

    [Fact]
    public void CpuHighPersistence_IsZero_WhenNoTicks()
    {
        var ctx = EmptyCtx(700);
        var features = RunSystem(ctx);
        Assert.Equal(0.0, features["cpu_high_persistence_h300"]);
    }

    [Fact]
    public void CpuHighPersistence_RatioOfHighCpuSamples()
    {
        // 3 ticks: cpu 90, 50, 90 → 2/3 high
        var ticks = H300Ticks(new[] { (90.0, 0.0, 0.0, 0.0), (50.0, 0.0, 0.0, 0.0), (90.0, 0.0, 0.0, 0.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.Equal(2.0 / 3.0, features["cpu_high_persistence_h300"], precision: 3);
    }

    [Fact]
    public void NetThroughputTrend_IsZero_WhenFewerThanTwoTicks()
    {
        var ticks = H300Ticks(new[] { (50.0, 0.0, 10.0, 5.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.Equal(0.0, features["net_throughput_trend_h300"]);
    }

    [Fact]
    public void NetThroughputTrend_IsPositive_WhenThroughputIncreases()
    {
        // Throughput goes 10, 20, 30 → slope positive
        var ticks = H300Ticks(new[] { (0.0, 0.0, 5.0, 5.0), (0.0, 0.0, 10.0, 10.0), (0.0, 0.0, 15.0, 15.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.True(features["net_throughput_trend_h300"] > 0.0);
    }

    [Fact]
    public void NetThroughputTrend_IsNegative_WhenThroughputDecreases()
    {
        var ticks = H300Ticks(new[] { (0.0, 0.0, 15.0, 15.0), (0.0, 0.0, 10.0, 10.0), (0.0, 0.0, 5.0, 5.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.True(features["net_throughput_trend_h300"] < 0.0);
    }

    // --- CrossTemporalAggregator ---

    [Fact]
    public void ActiveResourceMismatch_IsZero_WhenNoTicks()
    {
        var ctx = EmptyCtx(700);
        var features = RunCross(ctx);
        Assert.Equal(0.0, features["active_resource_mismatch_h300"]);
    }

    [Fact]
    public void ActiveResourceMismatch_CountsHighResourceDuringInactiveTime()
    {
        // Session: display on, never unlocked → session defaults to locked → not active_work
        // 2 ticks with cpu_pct=90 (high) during non-active time → mismatch ratio = 1.0
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var h300Start    = windowEnd.AddSeconds(-300);
        var ticks = new[]
        {
            Tick(h300Start.AddSeconds(10), 90.0, 0.0, 0.0, 0.0),
            Tick(h300Start.AddSeconds(20), 90.0, 0.0, 0.0, 0.0),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, ticks);
        var features = RunCross(ctx);
        Assert.Equal(1.0, features["active_resource_mismatch_h300"], precision: 3);
    }

    [Fact]
    public void ActiveResourceMismatch_IsZero_WhenHighResourceDuringActiveWork()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var h300Start    = windowEnd.AddSeconds(-300);
        // Display on + unlocked → active_work
        var signals = new List<FeatureSignal>
        {
            new(sessionStart.AddSeconds(1),  SignalEventType.DisplayOn,
                new Dictionary<string, string>()),
            new(sessionStart.AddSeconds(2),  SignalEventType.SessionUnlock,
                new Dictionary<string, string>()),
            Tick(h300Start.AddSeconds(10), 90.0, 0.0, 0.0, 0.0),
            Tick(h300Start.AddSeconds(20), 90.0, 0.0, 0.0, 0.0),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = RunCross(ctx);
        Assert.Equal(0.0, features["active_resource_mismatch_h300"], precision: 3);
    }

    // --- helpers ---

    private static TemporalWindowContext EmptyCtx(int ageSec)
    {
        var s = T("2026-06-01T10:00:00Z");
        return TemporalWindowContext.Build(s.AddSeconds(ageSec), s, Array.Empty<FeatureSignal>());
    }

    private static TemporalWindowContext CtxWithSignals(IReadOnlyList<FeatureSignal> signals, int ageSec)
    {
        var s = T("2026-06-01T10:00:00Z");
        return TemporalWindowContext.Build(s.AddSeconds(ageSec), s, signals);
    }

    // (cpu, mem, rx, tx) tuples — placed at evenly-spaced offsets inside h300
    private static IReadOnlyList<FeatureSignal> H300Ticks(IEnumerable<(double cpu, double mem, double rx, double tx)> items)
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var h300Start    = sessionStart.AddSeconds(700 - 300);
        return items.Select((item, i) =>
            Tick(h300Start.AddSeconds(i * 5 + 1), item.cpu, item.mem, item.rx, item.tx))
            .ToList();
    }

    private static FeatureSignal Tick(DateTimeOffset ts, double cpu, double mem, double rx, double tx) =>
        new(ts, SignalEventType.SystemResourceTick,
            new Dictionary<string, string>
            {
                ["cpu_pct"]       = cpu.ToString(),
                ["mem_used_pct"]  = mem.ToString(),
                ["net_rx_kbps"]   = rx.ToString(),
                ["net_tx_kbps"]   = tx.ToString(),
                ["cpu_available"] = "true",
                ["mem_available"] = "true"
            });

    private static Dictionary<string, double> RunSystem(TemporalWindowContext ctx) =>
        new SystemTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static Dictionary<string, double> RunCross(TemporalWindowContext ctx) =>
        new CrossTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
```

- [ ] **Step 2: Run — expect compile failure**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "SystemCrossTemporalAggregatorTests" -v n
```

- [ ] **Step 3: Implement `SystemTemporalAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/SystemTemporalAggregator.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class SystemTemporalAggregator : ITemporalAggregator
{
    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        var ticks = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.SystemResourceTick)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        // cpu_high_persistence_h300
        double cpuHigh = 0.0;
        if (ticks.Count > 0)
        {
            var cpu = ticks.Select(e => PayloadValueReader.GetDouble(e.Payload, "cpu_pct")).ToList();
            cpuHigh = FeatureMath.SafeDivide(cpu.Count(v => v >= 80.0), cpu.Count);
        }
        yield return Kv("cpu_high_persistence_h300", cpuHigh);

        // net_throughput_trend_h300 — OLS slope over sample index
        double trend = 0.0;
        if (ticks.Count >= 2)
        {
            var x = Enumerable.Range(0, ticks.Count).Select(i => (double)i).ToArray();
            var y = ticks.Select(e =>
                PayloadValueReader.GetDouble(e.Payload, "net_rx_kbps") +
                PayloadValueReader.GetDouble(e.Payload, "net_tx_kbps")).ToArray();
            trend = OlsSlope(x, y);
        }
        yield return Kv("net_throughput_trend_h300", trend);
    }

    private static double OlsSlope(double[] x, double[] y)
    {
        var meanX = x.Average();
        var meanY = y.Average();
        var num = x.Zip(y, (xi, yi) => (xi - meanX) * (yi - meanY)).Sum();
        var den = x.Sum(xi => (xi - meanX) * (xi - meanX));
        return den < 1e-9 ? 0.0 : num / den;
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
```

- [ ] **Step 4: Implement `CrossTemporalAggregator`**

```csharp
// src/FeatureExtraction/TemporalAggregator/CrossTemporalAggregator.cs
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class CrossTemporalAggregator : ITemporalAggregator
{
    private readonly SessionFeatureAggregator _sessionAgg = new();

    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        var h300Effective = ctx.WindowEnd.AddSeconds(-300) > ctx.SessionStart
            ? ctx.WindowEnd.AddSeconds(-300)
            : ctx.SessionStart;
        var h300Window    = new SlidingWindow(h300Effective, ctx.WindowEnd);
        var sessionResult = _sessionAgg.ExtractFeatures(ctx.SessionSignals, h300Window);
        var activeList    = CrossFeatureAggregator.BuildActiveWorkIntervals(sessionResult.Intervals);

        var h300Ticks = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.SystemResourceTick)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        double mismatch = 0.0;
        if (h300Ticks.Count > 0)
        {
            int count = 0;
            foreach (var tick in h300Ticks)
            {
                var cpu = PayloadValueReader.GetDouble(tick.Payload, "cpu_pct");
                var mem = PayloadValueReader.GetDouble(tick.Payload, "mem_used_pct");
                if (cpu < 80.0 && mem < 80.0) continue; // not high-resource

                var inActive = activeList.Any(a =>
                    tick.TimestampUtc >= a.StartUtc && tick.TimestampUtc < a.EndUtc);
                if (!inActive) count++;
            }
            mismatch = FeatureMath.SafeDivide(count, h300Ticks.Count);
        }

        yield return new("active_resource_mismatch_h300", mismatch);
    }
}
```

- [ ] **Step 5: Run — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "SystemCrossTemporalAggregatorTests" -v n
```

Expected: all 8 tests PASS.

- [ ] **Step 6: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/SystemTemporalAggregator.cs
git add src/FeatureExtraction/TemporalAggregator/CrossTemporalAggregator.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/SystemCrossTemporalAggregatorTests.cs
git commit -m "feat: add SystemTemporalAggregator + CrossTemporalAggregator (3 features)"
```

---

## Task 7: `TemporalFeatureExtractor`

**Files:**
- Create: `src/FeatureExtraction/TemporalAggregator/TemporalFeatureExtractor.cs`

No dedicated test file — covered by the end-to-end test in Task 10. The extractor is a thin wiring layer.

- [ ] **Step 1: Create `TemporalFeatureExtractor`**

```csharp
// src/FeatureExtraction/TemporalAggregator/TemporalFeatureExtractor.cs
namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class TemporalFeatureExtractor
{
    private readonly IReadOnlyList<ITemporalAggregator> _aggregators =
    [
        new TemporalMetadataAggregator(),
        new AppTemporalAggregator(),
        new SessionTemporalAggregator(),
        new NetworkTemporalAggregator(),
        new SystemTemporalAggregator(),
        new CrossTemporalAggregator()
    ];

    public Dictionary<string, double> Extract(TemporalWindowContext ctx)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var aggregator in _aggregators)
            foreach (var kv in aggregator.Aggregate(ctx))
                result[kv.Key] = kv.Value;
        return result;
    }
}
```

- [ ] **Step 2: Verify build passes**

```
dotnet build EndpointSignalAgent.sln
```

Expected: build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```
git add src/FeatureExtraction/TemporalAggregator/TemporalFeatureExtractor.cs
git commit -m "feat: add TemporalFeatureExtractor orchestrator"
```

---

## Task 8: `FeatureSchema.cs` — v1.3 additions

**Files:**
- Modify: `src/FeatureExtraction/SignalAggregator/FeatureSchema.cs`

Add the v1.3 version constant and the 18-column minimal set. Do not change `FeatureVersion` (that remains the v1.2 live-extraction constant).

- [ ] **Step 1: Add v1.3 definitions to `FeatureSchema.cs`**

Append after the `AllColumns` array (before the closing `}`):

```csharp
    public const string FeatureVersion13 = "1.3";

    public static readonly string[] TemporalColumns =
    {
        // Temporal metadata / quality
        "session_age_sec",
        "history_coverage_ratio_h300",
        "history_coverage_ratio_h600",
        "is_temporal_warmup_window",
        "raw_event_count_h300",
        // Application temporal
        "category_transition_count_h300",
        "category_transition_entropy_h300",
        "app_switch_interval_std_ms_h300",
        "app_dwell_cv_h300",
        // Session temporal
        "active_work_ratio_h300",
        "active_work_streak_sec",
        "time_since_unlock_sec",
        // Network temporal
        "network_change_count_h300",
        "time_since_network_change_sec",
        "network_context_stability_ratio_h600",
        // System temporal
        "cpu_high_persistence_h300",
        "net_throughput_trend_h300",
        // Cross temporal
        "active_resource_mismatch_h300"
    };
```

- [ ] **Step 2: Verify build and that AllColumns length is unchanged**

```
dotnet build EndpointSignalAgent.sln
```

Expected: 0 errors, `AllColumns.Length` still 99 (unchanged — `TemporalColumns` is a separate array).

- [ ] **Step 3: Commit**

```
git add src/FeatureExtraction/SignalAggregator/FeatureSchema.cs
git commit -m "feat: add FeatureSchema.TemporalColumns and FeatureVersion13 for v1.3"
```

---

## Task 9: `RawSignalReplayReader`

**Files:**
- Create: `src/FeatureExtraction/ReplayPipeline/RawSignalReplayReader.cs`

Reads `raw_signals.jsonl` line by line, deserialises each `RawCollectorSignalRecord`, and converts it to `FeatureSignal`. Silently skips blank lines and malformed JSON.

- [ ] **Step 1: Create `RawSignalReplayReader`**

```csharp
// src/FeatureExtraction/ReplayPipeline/RawSignalReplayReader.cs
using System.Runtime.CompilerServices;
using System.Text.Json;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.SignalCollection.Contracts;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.ReplayPipeline;

public sealed class RawSignalReplayReader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async IAsyncEnumerable<RawCollectorSignalRecord> ReadRecordsAsync(
        string rawSignalsPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var line in File.ReadLinesAsync(rawSignalsPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RawCollectorSignalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<RawCollectorSignalRecord>(line, JsonOpts);
            }
            catch (JsonException)
            {
                continue;
            }
            if (record is not null) yield return record;
        }
    }

    /// <summary>
    /// Returns only records belonging to the given session, as FeatureSignals sorted by timestamp.
    /// </summary>
    public async Task<List<FeatureSignal>> LoadSessionSignalsAsync(
        string rawSignalsPath,
        string sessionId,
        CancellationToken ct = default)
    {
        var signals = new List<FeatureSignal>();
        await foreach (var record in ReadRecordsAsync(rawSignalsPath, ct))
        {
            if (!string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
                continue;
            signals.Add(ToFeatureSignal(record));
        }
        signals.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return signals;
    }

    public static FeatureSignal ToFeatureSignal(RawCollectorSignalRecord record)
    {
        SignalEventTypeParser.TryParse(record.SignalType, out var type);
        return new FeatureSignal(
            record.TimestampUtc,
            type,
            record.Payload,
            record.NativeAggregationSec);
    }
}
```

- [ ] **Step 2: Verify build**

```
dotnet build EndpointSignalAgent.sln
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```
git add src/FeatureExtraction/ReplayPipeline/RawSignalReplayReader.cs
git commit -m "feat: add RawSignalReplayReader for raw_signals.jsonl deserialization"
```

---

## Task 10: `TemporalExtractionRunner` + end-to-end test

**Files:**
- Create: `src/FeatureExtraction/ReplayPipeline/TemporalExtractionRunner.cs`
- Create: `tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalExtractionRunnerTests.cs`

The runner takes:
- `sessionStart` / `sessionEnd` — collection-session boundaries (from session manifest)
- `sessionSignals` — pre-loaded `FeatureSignal` list for the session (use `RawSignalReplayReader.LoadSessionSignalsAsync`)
- `profile` — one of `WindowProfile.W60S30`, `W120S60`, `W30S15`

It emits one `(windowEnd, temporalFeatures)` pair per fully-contained window. "Fully-contained" means `window.StartUtc >= sessionStart AND window.EndUtc <= sessionEnd`.

- [ ] **Step 1: Write failing end-to-end test**

```csharp
// tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalExtractionRunnerTests.cs
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.ReplayPipeline;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class TemporalExtractionRunnerTests
{
    [Fact]
    public void Run_ProducesOneRowPerWindowInSession()
    {
        // W60S30 profile: windows every 30 s, each 60 s wide.
        // Session: [T+0, T+300). Windows that fit: start at T+0 through T+240 (end at T+60..T+300).
        // Expected count = (300-60)/30 + 1 = 9 windows.
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);

        var runner = new TemporalExtractionRunner();
        var results = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        Assert.Equal(9, results.Count);
    }

    [Fact]
    public void Run_AllRowsContainAllTemporalColumns()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);

        var runner  = new TemporalExtractionRunner();
        var results = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        foreach (var (_, features) in results)
        {
            foreach (var col in FeatureSchema.TemporalColumns)
                Assert.True(features.ContainsKey(col), $"Missing column: {col}");
        }
    }

    [Fact]
    public void Run_WindowEndsAreAlignedToProfile()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);
        var runner       = new TemporalExtractionRunner();
        var results      = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        // W60S30: windows aligned to 30 s multiples from Unix epoch
        foreach (var (windowEnd, _) in results)
        {
            var unixSec = windowEnd.ToUnixTimeSeconds();
            Assert.Equal(0, unixSec % 30);
        }
    }

    [Fact]
    public void Run_SessionAgeIncreasesAcrossWindows()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);
        var runner       = new TemporalExtractionRunner();
        var results      = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        var ages = results.Select(r => r.Features["session_age_sec"]).ToList();
        for (int i = 1; i < ages.Count; i++)
            Assert.True(ages[i] > ages[i - 1], $"session_age_sec should increase: [{i-1}]={ages[i-1]}, [{i}]={ages[i]}");
    }

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
```

- [ ] **Step 2: Run — expect compile failure**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "TemporalExtractionRunnerTests" -v n
```

- [ ] **Step 3: Implement `TemporalExtractionRunner`**

```csharp
// src/FeatureExtraction/ReplayPipeline/TemporalExtractionRunner.cs
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.ReplayPipeline;

public sealed class TemporalExtractionRunner
{
    private readonly TemporalFeatureExtractor _extractor = new();

    /// <summary>
    /// Enumerates windows for <paramref name="profile"/> that are fully contained within
    /// [<paramref name="sessionStart"/>, <paramref name="sessionEnd"/>] and returns one
    /// temporal feature dictionary per window.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset WindowEnd, Dictionary<string, double> Features)> Run(
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd,
        IReadOnlyList<FeatureSignal> sessionSignals,
        WindowProfile profile)
    {
        // Align first window start to step grid, then enumerate forward
        var firstStart = SlidingWindowing.AlignToStepUtc(sessionStart, profile.SlideSec);
        if (firstStart < sessionStart)
            firstStart = firstStart.AddSeconds(profile.SlideSec); // step forward to first valid start

        var lastStart = sessionEnd.AddSeconds(-profile.WindowSizeSec);
        lastStart = SlidingWindowing.AlignToStepUtc(lastStart, profile.SlideSec);

        var results = new List<(DateTimeOffset, Dictionary<string, double>)>();

        foreach (var window in SlidingWindowing.EnumerateWindowStarts(
            firstStart, lastStart, profile.WindowSizeSec, profile.SlideSec))
        {
            if (window.StartUtc < sessionStart || window.EndUtc > sessionEnd) continue;

            var ctx      = TemporalWindowContext.Build(window.EndUtc, sessionStart, sessionSignals);
            var features = _extractor.Extract(ctx);
            results.Add((window.EndUtc, features));
        }

        return results;
    }
}
```

- [ ] **Step 4: Run — expect PASS**

```
dotnet test tests/EndpointSignalAgent.Tests --filter "TemporalExtractionRunnerTests" -v n
```

Expected: all 4 tests PASS.

- [ ] **Step 5: Run full test suite**

```
dotnet test tests/EndpointSignalAgent.Tests -v n
```

Expected: all tests PASS, 0 failures.

- [ ] **Step 6: Commit**

```
git add src/FeatureExtraction/ReplayPipeline/TemporalExtractionRunner.cs
git add tests/EndpointSignalAgent.Tests/TemporalAggregator/TemporalExtractionRunnerTests.cs
git commit -m "feat: add TemporalExtractionRunner — end-to-end v1.3 replay pipeline"
```

---

## Self-review checklist

### Spec coverage

| Spec section | Covered by |
|---|---|
| § 2 session_age_sec | Task 2: TemporalMetadataAggregator |
| § 2 history_coverage_ratio_h300/h600 | Task 2 |
| § 2 is_temporal_warmup_window | Task 2 |
| § 2 raw_event_count_h300 | Task 2 |
| § 3 category_transition_count_h300 | Task 3 |
| § 3 category_transition_entropy_h300 | Task 3 |
| § 3 app_switch_interval_std_ms_h300 | Task 3 |
| § 3 app_dwell_cv_h300 | Task 3 |
| § 4 active_work_ratio_h300 | Task 4 |
| § 4 active_work_streak_sec | Task 4 |
| § 4 time_since_unlock_sec | Task 4 |
| § 5 network_change_count_h300 | Task 5 |
| § 5 time_since_network_change_sec | Task 5 |
| § 5 network_context_stability_ratio_h600 | Task 5 |
| § 6 cpu_high_persistence_h300 | Task 6 |
| § 6 net_throughput_trend_h300 | Task 6 |
| § 7 active_resource_mismatch_h300 | Task 6 |
| Design rule: no session boundary crossing | TemporalWindowContext.Build clips all horizons to SessionStart |
| Design rule: initial network snapshots excluded | NetworkTemporalAggregator.IsNonInitial filters payload["initial"]=="true" |
| Supported profiles: W30S15, W60S30, W120S60 | WindowProfile.DefaultProfiles already defined; TemporalExtractionRunner accepts any profile |
| § 8 minimal set only | TemporalColumns array has exactly the 18 features listed in § 8 |

### Features NOT in minimal set (§8) — not implemented

`dominant_category_persistence_h600`, `idle_transition_count_h300`, `vpn_state_persistence_h300`, `public_ip_change_burst_h600`, `ram_high_persistence_h300`, `resource_spike_streak_h300`, `resource_variability_h300`, `active_switching_intensity_h300`, `active_category_entropy_h300` — out of scope for this plan.

### Notes for implementer

1. **`SessionTemporalAggregator` uses `CrossFeatureAggregator.BuildActiveWorkIntervals`** which is `internal` to `EndpointSignalAgent.FeatureExtraction.SignalAggregator`. Both the aggregator and the temporal files share the same assembly so access is fine — no visibility change required.
2. **`IReadOnlyList<FeatureSignal>` vs `List<FeatureSignal>`**: `TemporalWindowContext.Build` accepts `IReadOnlyList<FeatureSignal>`. Pass a sorted list; the builder does not re-sort.
3. **`FeatureMath.StdDev`** uses population std (denominator N). Tests are written accordingly.
4. **Window alignment edge case**: `SlidingWindowing.AlignToStepUtc` floors to the step boundary. If `sessionStart` is already on a boundary, `firstStart = sessionStart`. If not, the runner steps forward by one `SlideSec` to get the first window that starts within the session.
