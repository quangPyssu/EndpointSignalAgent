# Sleep / Lock Collection-Gap Handling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Slow polling collectors when the session is locked, detect sleep/hibernate as a collection gap, and prevent feature windows that overlap that gap from being scored as normal, abnormal, or attack behaviour.

**Architecture:** Extend `ICollectionControl` with an `IsSessionLocked` flag (wired from `SessionStateCollector` on every lock/unlock). The three polling collectors check this flag and pace themselves to 30 s when locked, mirroring the existing idle-loop cadence in `SessionStateCollector`. `SessionEventWindowListener` receives two new `WM_POWERBROADCAST` message IDs (`PBT_APMSUSPEND` / `PBT_APMRESUMEAUTOMATIC`); `SessionStateCollector` emits `PowerSuspend`, `PowerResume`, and `CollectionGapDetected` signals. `FeatureExtractorService` tracks gap intervals and a post-resume warm-up window and stamps every feature row with `has_collection_gap` (1/0) and `in_warm_up` (1/0) so the scoring layer can skip them.

**Tech Stack:** C# 12 / .NET 8, xUnit, `System.Threading.Interlocked`, Win32 `WM_POWERBROADCAST`

---

## Build command (macOS / cross-platform)
```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

## Test command (Windows only)
```bash
dotnet test tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj
```

---

## File Map

| Action | Path |
|---|---|
| Modify | `src/Shared/Contracts/SignalEvent.cs` |
| Modify | `src/SignalCollection/Services/CollectionControl.cs` |
| Modify | `src/SignalCollection/Collectors/SessionStateCollector.cs` |
| Modify | `src/SignalCollection/Collectors/SystemResourceCollector.cs` |
| Modify | `src/SignalCollection/Collectors/NetworkContextCollector.cs` |
| Modify | `src/SignalCollection/Collectors/ApplicationUsageCollector.cs` |
| Modify | `src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs` |
| Modify | `src/FeatureExtraction/Services/FeatureExtractorService.cs` |
| Create | `tests/EndpointSignalAgent.Tests/CollectionControlLockTests.cs` |
| Create | `tests/EndpointSignalAgent.Tests/CollectionGapFlaggingTests.cs` |

---

## Task 1: New signal types + `IsSessionLocked` in `ICollectionControl`

**Files:**
- Modify: `src/Shared/Contracts/SignalEvent.cs`
- Modify: `src/SignalCollection/Services/CollectionControl.cs`
- Modify: `src/FeatureExtraction/Services/FeatureExtractorService.cs` (preserve set only)
- Create: `tests/EndpointSignalAgent.Tests/CollectionControlLockTests.cs`

- [ ] **Step 1: Write failing tests for `IsSessionLocked`**

Create `tests/EndpointSignalAgent.Tests/CollectionControlLockTests.cs`:

```csharp
using EndpointSignalAgent.SignalCollection.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class CollectionControlLockTests
{
    [Fact]
    public void IsSessionLocked_DefaultsFalse()
    {
        var control = new CollectionControl();
        Assert.False(control.IsSessionLocked);
    }

    [Fact]
    public void SetSessionLocked_True_ReflectsInProperty()
    {
        var control = new CollectionControl();
        control.SetSessionLocked(true);
        Assert.True(control.IsSessionLocked);
    }

    [Fact]
    public void SetSessionLocked_False_ClearsLock()
    {
        var control = new CollectionControl();
        control.SetSessionLocked(true);
        control.SetSessionLocked(false);
        Assert.False(control.IsSessionLocked);
    }

    [Fact]
    public void IsSessionLocked_DoesNotAffectIsPaused()
    {
        var control = new CollectionControl();
        control.SetSessionLocked(true);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public void IsPaused_DoesNotAffectIsSessionLocked()
    {
        var control = new CollectionControl();
        control.Pause();
        Assert.False(control.IsSessionLocked);
    }
}
```

- [ ] **Step 2: Build to confirm tests compile (will fail — `IsSessionLocked` not yet defined)**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: build error — `IsSessionLocked` does not exist on `ICollectionControl`.

- [ ] **Step 3: Add `IsSessionLocked` + `SetSessionLocked` to `ICollectionControl` and `CollectionControl`**

Replace the entire `src/SignalCollection/Services/CollectionControl.cs`:

```csharp
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
```

- [ ] **Step 4: Add `PowerSuspend`, `PowerResume`, `CollectionGapDetected` to `SignalEventType`**

In `src/Shared/Contracts/SignalEvent.cs`, add three new members after `SystemResourceTick`:

```csharp
    SystemResourceTick,

    PowerSuspend,
    PowerResume,
    CollectionGapDetected
```

- [ ] **Step 5: Add new types to `CompactBufferLocked` preserve set in `FeatureExtractorService`**

In `src/FeatureExtraction/Services/FeatureExtractorService.cs`, find `CompactBufferLocked` and update the `preserveStateTypes` set to include:

```csharp
var preserveStateTypes = new HashSet<SignalEventType>
{
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

- [ ] **Step 6: Build**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 7: Commit**

```bash
git add src/Shared/Contracts/SignalEvent.cs \
        src/SignalCollection/Services/CollectionControl.cs \
        src/FeatureExtraction/Services/FeatureExtractorService.cs \
        tests/EndpointSignalAgent.Tests/CollectionControlLockTests.cs
git commit -m "feat: add PowerSuspend/PowerResume/CollectionGapDetected signal types; add IsSessionLocked to ICollectionControl"
```

---

## Task 2: Wire lock state from `SessionStateCollector` into `CollectionControl`

**Files:**
- Modify: `src/SignalCollection/Collectors/SessionStateCollector.cs`

The `SessionStateCollector` already knows when the session locks/unlocks (it calls `OnSessionStateChanged`). This task stores a local reference to `ICollectionControl` and calls `SetSessionLocked` whenever the debounced lock state transitions.

- [ ] **Step 1: Store `ICollectionControl` locally in `SessionStateCollector`**

In `src/SignalCollection/Collectors/SessionStateCollector.cs`, add a field after the existing field declarations (around line 32):

```csharp
private readonly ICollectionControl _collectionControl;
```

Update the constructor body (after the `: base(...)` call) to store it:

```csharp
public SessionStateCollector(
    ILogger<SessionStateCollector> logger,
    ISignalBroadcaster broadcaster,
    ICollectionControl collectionControl)
    : base(@"spool\signals.jsonl", broadcaster, collectionControl)
{
    _logger = logger;
    _collectionControl = collectionControl;
}
```

- [ ] **Step 2: Call `SetSessionLocked` from `OnSessionStateChanged`**

In `OnSessionStateChanged`, directly after `_isSessionLocked = stableLockState;` (the line that updates the local field), add:

```csharp
_isSessionLocked = stableLockState;
_collectionControl.SetSessionLocked(stableLockState);
```

The method after the change should look like:

```csharp
private void OnSessionStateChanged(SessionStateChange change)
{
    if (change.Source == "WTS")
    {
        _lastWtsLockEventUtc = DateTimeOffset.UtcNow;
    }

    if (!_sessionLockDebouncer.TryTransition(change.IsLocked, DateTimeOffset.UtcNow, out var stableLockState))
    {
        return;
    }

    _isSessionLocked = stableLockState;
    _collectionControl.SetSessionLocked(stableLockState);

    EnqueueSignal(
        stableLockState ? SignalEventType.SessionLock : SignalEventType.SessionUnlock,
        new Dictionary<string, string>
        {
            ["source"] = change.Source,
            ["reason"] = change.Reason
        });
}
```

- [ ] **Step 3: Build**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/SignalCollection/Collectors/SessionStateCollector.cs
git commit -m "feat: wire SessionStateCollector lock/unlock into CollectionControl.SetSessionLocked"
```

---

## Task 3: Locked cadence for polling collectors

**Files:**
- Modify: `src/SignalCollection/Collectors/SystemResourceCollector.cs`
- Modify: `src/SignalCollection/Collectors/NetworkContextCollector.cs`
- Modify: `src/SignalCollection/Collectors/ApplicationUsageCollector.cs`

Pattern: keep `PeriodicTimer` at the fast rate, track `_lastSampleUtc`, gate work on elapsed time vs. the current cadence. When `IsSessionLocked`, use 30 s cadence. This mirrors the existing pattern in `SessionStateCollector.IdleAndScreenSaverLoop`.

- [ ] **Step 1: Add locked cadence to `SystemResourceCollector`**

In `src/SignalCollection/Collectors/SystemResourceCollector.cs`:

Add a field and store the control ref:

```csharp
private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
private readonly TimeSpan _lockedPollInterval = TimeSpan.FromSeconds(30);
private readonly ICollectionControl _collectionControl;
```

Update constructor to store it (add `_collectionControl = collectionControl;` before the sampler line):

```csharp
public SystemResourceCollector(
    ILogger<SystemResourceCollector> logger,
    ISignalBroadcaster broadcaster,
    ICollectionControl collectionControl)
    : base(@"spool\signals.jsonl", broadcaster, collectionControl)
{
    _logger = logger;
    _collectionControl = collectionControl;
    _sampler = new SystemResourceSampler(logger);
}
```

Replace `ExecuteAsync` with the time-gated version:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    Directory.CreateDirectory("spool");
    _logger.LogInformation("SystemResourceCollector started.");

    using var timer = new PeriodicTimer(_pollInterval);
    var lastSampleUtc = DateTimeOffset.MinValue;

    try
    {
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var cadence = _collectionControl.IsSessionLocked ? _lockedPollInterval : _pollInterval;
                if ((now - lastSampleUtc) < cadence)
                    continue;

                lastSampleUtc = now;
                var sample = _sampler.CaptureSample(now);
                await WriteSignalAsync(SignalEventType.SystemResourceTick, BuildPayload(sample));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemResourceCollector loop error.");
            }
        }
    }
    finally
    {
        _sampler.Dispose();
    }

    _logger.LogInformation("SystemResourceCollector stopped.");
}
```

- [ ] **Step 2: Add locked cadence to `NetworkContextCollector`**

In `src/SignalCollection/Collectors/NetworkContextCollector.cs`:

Add a field:

```csharp
private readonly TimeSpan _poll = TimeSpan.FromSeconds(3);
private readonly TimeSpan _lockedPoll = TimeSpan.FromSeconds(30);
private readonly ICollectionControl _collectionControl;
```

In the `internal` constructor, add `_collectionControl = collectionControl;` as the first line of the body (before the null-coalescing assignments).

Replace the main polling loop in `ExecuteAsync` (the `using var timer` block, starting around line 144):

```csharp
using var timer = new PeriodicTimer(_poll);
var lastSampleUtc = DateTimeOffset.MinValue;

while (await timer.WaitForNextTickAsync(stoppingToken))
{
    try
    {
        var now = _clock.UtcNow;
        var cadence = _collectionControl.IsSessionLocked ? _lockedPoll : _poll;
        if ((now - lastSampleUtc) < cadence)
            continue;

        lastSampleUtc = now;
        await RefreshPublicIpAsync(stoppingToken, force: false);
        var tick = BuildTickState(now);
        await ProcessTickAsync(tick, now);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "NetworkContextCollector loop error.");
    }
}
```

- [ ] **Step 3: Add locked cadence to `ApplicationUsageCollector` fallback poll**

In `src/SignalCollection/Collectors/ApplicationUsageCollector.cs`:

Add a field (alongside the existing interval fields):

```csharp
private readonly TimeSpan _fallbackPollInterval = TimeSpan.FromSeconds(3);
private readonly TimeSpan _lockedFallbackPollInterval = TimeSpan.FromSeconds(30);
private readonly ICollectionControl _collectionControl;
```

In the `internal` constructor, add `_collectionControl = collectionControl;` to the constructor body.

Replace `RunFallbackPollingAsync`:

```csharp
private async Task RunFallbackPollingAsync(CancellationToken stoppingToken)
{
    try
    {
        using var timer = new PeriodicTimer(_fallbackPollInterval);
        var lastPollUtc = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = _clock.UtcNow;
            var cadence = _collectionControl.IsSessionLocked
                ? _lockedFallbackPollInterval
                : _fallbackPollInterval;
            if ((now - lastPollUtc) < cadence)
                continue;

            lastPollUtc = now;
            _input.Writer.TryWrite(InputMessage.FromObservation(_foregroundSource.Poll(now, "poll")));
        }
    }
    catch (OperationCanceledException)
    {
        // normal shutdown
    }
}
```

- [ ] **Step 4: Build**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/SignalCollection/Collectors/SystemResourceCollector.cs \
        src/SignalCollection/Collectors/NetworkContextCollector.cs \
        src/SignalCollection/Collectors/ApplicationUsageCollector.cs
git commit -m "feat: slow polling collectors to 30s cadence when session is locked (mirrors SessionStateCollector idle loop pattern)"
```

---

## Task 4: `PowerSuspend` / `PowerResume` / `CollectionGapDetected` signals

**Files:**
- Modify: `src/SignalCollection/Collectors/SessionStateCollector.cs`

`SessionEventWindowListener` is a private nested class inside `SessionStateCollector.cs`. It already handles `WM_POWERBROADCAST` for `PBT_POWERSETTINGCHANGE`. Add two new constants and two callbacks for `PBT_APMSUSPEND` (machine going to sleep) and `PBT_APMRESUMEAUTOMATIC` (machine woke). `SessionStateCollector` tracks the suspend timestamp and emits three signals on resume.

- [ ] **Step 1: Add suspend/resume callbacks to `SessionEventWindowListener`**

In `SessionStateCollector.cs`, find the private `SessionEventWindowListener` nested class.

Add two constants after the existing power constants (near `PBT_POWERSETTINGCHANGE`):

```csharp
private const int PBT_APMSUSPEND = 0x0004;
private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
```

Add two callback fields in the listener (alongside `_onDisplayChange`, `_onSessionChange`, `_onPresenceChange`):

```csharp
private readonly Action _onPowerSuspend;
private readonly Action _onPowerResume;
```

Update the `SessionEventWindowListener` constructor signature and body to accept and store them:

```csharp
internal SessionEventWindowListener(
    ILogger logger,
    Action<DisplayStateChange> onDisplayChange,
    Action<SessionStateChange> onSessionChange,
    Action<PresenceChange> onPresenceChange,
    Action onPowerSuspend,
    Action onPowerResume)
{
    _logger = logger;
    _onDisplayChange = onDisplayChange;
    _onSessionChange = onSessionChange;
    _onPresenceChange = onPresenceChange;
    _onPowerSuspend = onPowerSuspend;
    _onPowerResume = onPowerResume;
}
```

- [ ] **Step 2: Handle `PBT_APMSUSPEND` and `PBT_APMRESUMEAUTOMATIC` in `WndProcImpl`**

In `WndProcImpl`, the existing check is:

```csharp
if ((int)msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE)
{
    ParsePowerSettingChange(lParam);
    return new IntPtr(1);
}
```

Replace it with:

```csharp
if ((int)msg == WM_POWERBROADCAST)
{
    var eventId = (int)wParam;
    if (eventId == PBT_POWERSETTINGCHANGE)
    {
        ParsePowerSettingChange(lParam);
        return new IntPtr(1);
    }
    if (eventId == PBT_APMSUSPEND)
    {
        _onPowerSuspend();
        return IntPtr.Zero;
    }
    if (eventId == PBT_APMRESUMEAUTOMATIC)
    {
        _onPowerResume();
        return IntPtr.Zero;
    }
}
```

- [ ] **Step 3: Add suspend tracking + signal emission to `SessionStateCollector`**

In the outer `SessionStateCollector` class, add a field after `_lastWtsLockEventUtc`:

```csharp
private DateTimeOffset? _lastSuspendUtc;
```

Add two new private methods to `SessionStateCollector`:

```csharp
private void OnPowerSuspend()
{
    _lastSuspendUtc = DateTimeOffset.UtcNow;
    EnqueueSignal(SignalEventType.PowerSuspend, new Dictionary<string, string>
    {
        ["reason"] = "PBT_APMSUSPEND"
    });
}

private void OnPowerResume()
{
    var now = DateTimeOffset.UtcNow;

    var resumePayload = new Dictionary<string, string>
    {
        ["reason"] = "PBT_APMRESUMEAUTOMATIC"
    };

    if (_lastSuspendUtc.HasValue)
    {
        var gapSec = (int)(now - _lastSuspendUtc.Value).TotalSeconds;
        resumePayload["suspendUtc"] = _lastSuspendUtc.Value.ToString("o");
        resumePayload["gapSec"] = gapSec.ToString();
    }

    EnqueueSignal(SignalEventType.PowerResume, resumePayload);

    if (_lastSuspendUtc.HasValue)
    {
        var gapSec = (int)(now - _lastSuspendUtc.Value).TotalSeconds;
        EnqueueSignal(SignalEventType.CollectionGapDetected, new Dictionary<string, string>
        {
            ["gapStartUtc"] = _lastSuspendUtc.Value.ToString("o"),
            ["gapEndUtc"] = now.ToString("o"),
            ["gapSec"] = gapSec.ToString(),
            ["reason"] = "sleep"
        });
        _lastSuspendUtc = null;
    }
}
```

- [ ] **Step 4: Update `StartSessionAndDisplayWatcher` to pass the two new callbacks**

Find `StartSessionAndDisplayWatcher` and update the `SessionEventWindowListener` constructor call:

```csharp
private void StartSessionAndDisplayWatcher()
{
    try
    {
        _sessionAndDisplayListener = new SessionEventWindowListener(
            _logger,
            OnDisplayStateChanged,
            OnSessionStateChanged,
            OnUserPresenceChanged,
            OnPowerSuspend,
            OnPowerResume);
        _sessionAndDisplayListener.Start();
        _logger.LogInformation("SessionStateCollector: session/display hidden-window watcher started");
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to start hidden-window session/display watcher (continuing with fallback paths)");
    }
}
```

- [ ] **Step 5: Build**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add src/SignalCollection/Collectors/SessionStateCollector.cs
git commit -m "feat: emit PowerSuspend/PowerResume/CollectionGapDetected signals via PBT_APMSUSPEND/PBT_APMRESUMEAUTOMATIC in SessionEventWindowListener"
```

---

## Task 5: Gap/warm-up flagging in `FeatureExtractorService`

**Files:**
- Modify: `src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs`
- Modify: `src/FeatureExtraction/Services/FeatureExtractorService.cs`
- Create: `tests/EndpointSignalAgent.Tests/CollectionGapFlaggingTests.cs`

Feature windows that overlap a collection gap get `has_collection_gap = 1.0`. Windows whose `StartUtc` falls within the warm-up period after `PowerResume` get `in_warm_up = 1.0`. Scoring consumers filter `WHERE has_collection_gap = 0 AND in_warm_up = 0`.

- [ ] **Step 1: Write failing tests for gap overlap and warm-up logic**

Create `tests/EndpointSignalAgent.Tests/CollectionGapFlaggingTests.cs`:

```csharp
using EndpointSignalAgent.FeatureExtraction.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class CollectionGapFlaggingTests
{
    // WindowOverlapsGap: gap fully inside window
    [Fact]
    public void WindowOverlapsGap_GapInsideWindow_ReturnsTrue()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var gapStart    = DateTimeOffset.Parse("2026-01-01T10:00:15Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T10:00:45Z");

        Assert.True(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            new[] { (gapStart, gapEnd) }));
    }

    // WindowOverlapsGap: gap entirely before window
    [Fact]
    public void WindowOverlapsGap_GapBeforeWindow_ReturnsFalse()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var gapStart    = DateTimeOffset.Parse("2026-01-01T09:58:00Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T09:59:00Z");

        Assert.False(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            new[] { (gapStart, gapEnd) }));
    }

    // WindowOverlapsGap: gap straddles window start
    [Fact]
    public void WindowOverlapsGap_GapStraddlesWindowStart_ReturnsTrue()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var gapStart    = DateTimeOffset.Parse("2026-01-01T09:59:30Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T10:00:30Z");

        Assert.True(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            new[] { (gapStart, gapEnd) }));
    }

    // WindowOverlapsGap: empty gap list
    [Fact]
    public void WindowOverlapsGap_NoGaps_ReturnsFalse()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");

        Assert.False(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            Array.Empty<(DateTimeOffset, DateTimeOffset)>()));
    }

    // InWarmUp: window starts before warm-up expiry
    [Fact]
    public void InWarmUp_WindowStartBeforeExpiry_ReturnsTrue()
    {
        var windowStart  = DateTimeOffset.Parse("2026-01-01T10:00:10Z");
        var warmUpUntil  = DateTimeOffset.Parse("2026-01-01T10:00:30Z");

        Assert.True(FeatureExtractorService.IsInWarmUp(windowStart, warmUpUntil));
    }

    // InWarmUp: window starts after warm-up expiry
    [Fact]
    public void InWarmUp_WindowStartAfterExpiry_ReturnsFalse()
    {
        var windowStart  = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var warmUpUntil  = DateTimeOffset.Parse("2026-01-01T10:00:30Z");

        Assert.False(FeatureExtractorService.IsInWarmUp(windowStart, warmUpUntil));
    }
}
```

- [ ] **Step 2: Build to confirm tests compile (will fail — methods don't exist yet)**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: build error — `WindowOverlapsGap` and `IsInWarmUp` do not exist on `FeatureExtractorService`.

- [ ] **Step 3: Add `WarmUpAfterResumeSec` to `FeatureExtractorOptions`**

In `src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs`, add after `EnableLiveExtraction`:

```csharp
/// <summary>
/// Seconds to flag feature windows as in_warm_up after a PowerResume event.
/// Windows flagged in_warm_up are excluded from rolling risk scoring.
/// </summary>
public int WarmUpAfterResumeSec { get; set; } = 30;
```

- [ ] **Step 4: Add gap/warm-up tracking fields and helper methods to `FeatureExtractorService`**

In `src/FeatureExtraction/Services/FeatureExtractorService.cs`, add new fields after the existing private fields (after `_liveExtractionRunId`):

```csharp
private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _collectionGaps = new();
private DateTimeOffset _warmUpUntilUtc = DateTimeOffset.MinValue;
private readonly object _gapLock = new();
```

Add two `internal static` helper methods (used by tests and by `TryEmitDueWindowsAsync`). Place them near the bottom of the class, before the closing brace:

```csharp
internal static bool WindowOverlapsGap(
    DateTimeOffset windowStart,
    DateTimeOffset windowEnd,
    IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> gaps)
    => gaps.Any(g => g.Start < windowEnd && g.End > windowStart);

internal static bool IsInWarmUp(DateTimeOffset windowStart, DateTimeOffset warmUpUntilUtc)
    => windowStart < warmUpUntilUtc;
```

- [ ] **Step 5: Process `CollectionGapDetected` and `PowerResume` signals in `ProcessSignal`**

In `ProcessSignal`, after the buffer/watermark update block, add:

```csharp
if (signal.Type == SignalEventType.CollectionGapDetected)
{
    if (signal.Payload.TryGetValue("gapStartUtc", out var startStr) &&
        signal.Payload.TryGetValue("gapEndUtc", out var endStr) &&
        DateTimeOffset.TryParse(startStr, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var gapStart) &&
        DateTimeOffset.TryParse(endStr, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var gapEnd))
    {
        lock (_gapLock)
        {
            _collectionGaps.Add((gapStart, gapEnd));
        }
    }
}

if (signal.Type == SignalEventType.PowerResume)
{
    lock (_gapLock)
    {
        _warmUpUntilUtc = signal.TimestampUtc
            + TimeSpan.FromSeconds(_options.Value.WarmUpAfterResumeSec);
    }
}
```

- [ ] **Step 6: Stamp `has_collection_gap` and `in_warm_up` onto each emitted window**

In `TryEmitDueWindowsAsync`, find the `foreach (var job in jobs)` loop. After `var features = ExtractWindowFeatures(job.Context, job.Window);`, add:

```csharp
var features = ExtractWindowFeatures(job.Context, job.Window);

bool hasGap;
bool inWarmUp;
lock (_gapLock)
{
    hasGap = WindowOverlapsGap(job.Window.StartUtc, job.Window.EndUtc, _collectionGaps);
    inWarmUp = IsInWarmUp(job.Window.StartUtc, _warmUpUntilUtc);

    // Prune gaps that ended before the previous window — no future window can overlap them.
    var pruneBeforeUtc = job.Window.StartUtc - TimeSpan.FromSeconds(FeatureSchema.WindowSec * 2);
    _collectionGaps.RemoveAll(g => g.End < pruneBeforeUtc);
}

features["has_collection_gap"] = hasGap ? 1.0 : 0.0;
features["in_warm_up"] = inWarmUp ? 1.0 : 0.0;
```

- [ ] **Step 7: Build**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 8: Add `QualityColumns` to `FeatureSchema` and bump `FeatureVersion`**

In `src/FeatureExtraction/SignalAggregator/FeatureSchema.cs`:

Add a new column array before `AllColumns`:

```csharp
public static readonly string[] QualityColumns =
[
    "has_collection_gap",
    "in_warm_up"
];
```

Append it to `AllColumns` (adapt to the existing concat syntax):

```csharp
public static readonly string[] AllColumns = AppColumns
    .Concat(SessionColumns)
    .Concat(NetworkColumns)
    .Concat(CrossColumns)
    .Concat(SystemColumns)
    .Concat(QualityColumns)
    .ToArray();
```

Bump `FeatureVersion` from `"1.2"` to `"1.2.1"`:

```csharp
public const string FeatureVersion = "1.2.1";
```

Without this, `FeatureCsvRowSerializer` — which iterates `AllColumns` by name to build header and rows — will silently omit both quality flags from every CSV payload.

- [ ] **Step 9: Build**

```bash
dotnet build tests/EndpointSignalAgent.Tests/EndpointSignalAgent.Tests.csproj -p:EnableWindowsTargeting=true
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 10: Commit**

```bash
git add src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs \
        src/FeatureExtraction/Services/FeatureExtractorService.cs \
        src/FeatureExtraction/SignalAggregator/FeatureSchema.cs \
        tests/EndpointSignalAgent.Tests/CollectionGapFlaggingTests.cs
git commit -m "feat: stamp has_collection_gap and in_warm_up onto feature windows; add QualityColumns to FeatureSchema; bump FeatureVersion to 1.2.1"
```
