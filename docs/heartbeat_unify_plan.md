# Plan: Unify Heartbeat Generation to Single Liveness-Gated Path

## Problem

`AppDwellReplayPreprocessor` currently has two paths:

- **Unconditional** (`GenerateHeartbeats`) — triggered when `AppDwell` closes the dwell
- **Liveness-gated** (`GenerateHeartbeatsGated`) — triggered for crash/unclosed dwells

The unconditional path is wrong for old files. If the machine slept during a dwell, the
process survived sleep, and the dwell eventually closed with a real `AppDwell` — the
unconditional path generates heartbeats across the entire sleep gap. No `PowerSuspend` /
`CollectionGapDetected` signals exist in old files to detect this, so the only available
signal is `SystemResourceTick` going silent during the gap.

## Why Collapsing to Gated is Safe

For any dwell where the agent was actually alive, `SystemResourceTick` fires every 2s.
The liveness gate checks a 60s backward window per heartbeat slot — during active use this
always passes. The only slots that fail are ones where ticks went silent (sleep/crash).

For short AppDwell-confirmed dwells with no other signals, `ForegroundAppChanged` at
`dwellStart` falls within the 60s liveness window of the first 4 heartbeat slots
(T0+15 through T0+60). So gating produces the same output as the unconditional path
for all normal short dwells. No test breaks for this reason.

## Changes

### 1. `AppDwellReplayPreprocessor.cs`

- Delete `GenerateHeartbeats` (unconditional method)
- In the `AppDwell` close branch: replace `GenerateHeartbeats(...)` with
  `GenerateHeartbeatsGated(..., signals)` — pass the full signal list
- Rename `GenerateHeartbeatsGated` → `GenerateHeartbeats` (it is now the only path)
- Update the xmldoc summary to remove the two-path description

The loop body becomes:

```csharp
else if (signal.Type == SignalEventType.AppDwell && currentAppKey is not null)
{
    var dwellAppKey = PayloadValueReader.GetString(signal.Payload, "appKey", "");
    if (string.Equals(dwellAppKey, currentAppKey, StringComparison.Ordinal))
    {
        synthetic.AddRange(GenerateHeartbeats(          // now always gated
            currentAppKey, currentCategory!, currentConfidence!,
            currentDwellStart, signal.TimestampUtc, signals));
        currentAppKey = null;
    }
}
```

The implicit-close and unclosed-end branches already call `GenerateHeartbeatsGated` —
they just need to be updated to call the renamed `GenerateHeartbeats`.

### 2. `AppDwellReplayPreprocessorTests.cs`

One test needs its description updated — the premise changes:

**`InjectHeartbeats_ClosedDwell_AlwaysGeneratesHeartbeatsRegardlessOfOtherSignals`**

Rename to `InjectHeartbeats_ClosedDwell_GeneratesHeartbeatsViaDwellStartLiveness` and
update the comment: heartbeats still emit because `ForegroundAppChanged` at T0 is within
the 60s liveness window of T0+15 and T0+30 — not because AppDwell triggers unconditional
generation.

All other tests pass unchanged because:
- Short dwells: ForegroundAppChanged covers the early slots
- Dwells with SystemTicks: liveness passes at every slot as before
- Gap/crash tests: already use gated logic

### 3. No changes needed elsewhere

- `AppFeatureAggregator` — consumes heartbeats from context, doesn't care how generated
- `FeatureExtractorService` — calls `InjectHeartbeats` unchanged
- `ApplicationUsageStateMachine` — live path, unaffected by replay preprocessor

## Behaviour Change Summary

| Scenario | Before | After |
|---|---|---|
| Normal dwell, active use | Unconditional heartbeats throughout | Gated — same output (SystemTick present) |
| Dwell spanning sleep gap (old file) | Heartbeats across entire sleep period | Heartbeats stop when ticks go silent, resume when ticks resume |
| Crash gap (implicit close) | Gated — already correct | Unchanged |
| Unclosed at end of file | Gated — already correct | Unchanged |
| Short dwell, no SystemTick | Unconditional heartbeats | Gated via ForegroundAppChanged liveness — same output |
