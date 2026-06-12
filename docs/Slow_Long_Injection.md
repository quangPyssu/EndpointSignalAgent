---
  Root cause: two separate issues

  1. allSignals is inflated — making every context scan linear-slow

  AppDwellReplayPreprocessor.InjectHeartbeats generates one heartbeat every 15 seconds for every dwell in the file — both closed and open — not just the currently-unclosed dwell. For a
  multi-hour file:

  - 50 app switches × avg 3-min dwell × 12 heartbeats/dwell = 600 extra signals minimum
  - An unclosed dwell spanning a long gap (e.g. agent crashed before FlushShutdownAsync, or first signal in a session is a ForegroundAppChanged that started hours earlier) → hundreds or
  thousands of heartbeats for that single gap

  ExtractFeaturesFromFileAsync then does this for every window across all 3 profiles:

  var context = allSignals
      .Where(e => e.TimestampUtc >= historyStart && e.TimestampUtc < window.EndUtc)  // ← scans ALL allSignals
      .Where(e => ShouldIncludeSignal(e, profile))
      .ToList();

  allSignals is sorted, but the code does a linear O(N) scan for every window. After injection, N is 2–4× larger. With DefaultProfiles having 3 profiles and e.g. 2,880 windows per day for W30S15
  alone:

  Before: 3,360 windows × 1,000 signals = 3.36M comparisons
  After:  3,360 windows × 3,500 signals = 11.8M comparisons  (3.5× slower)

  2. More rows written to the .db

  The context.Count == 0 guard at line 431 of FeatureExtractorService.cs:

  if (context.Count == 0)
  {
      continue;  // ← window skipped entirely
  }

  Before injection, windows during long sustained-focus periods with sparse non-app signals (no SystemResourceTick, no session events) could have empty context and be skipped. After injection,
  heartbeats every 15 s ensure almost no window has an empty context — those windows now get a row written to the db.

  The worst case is an unclosed dwell spanning a long gap between sessions: the preprocessor generates heartbeats every 15 s from ForegroundAppChanged(appA) all the way to the next
  ForegroundAppChanged (which could be hours later), filling every 30-second window during that gap with a heartbeat. Each of those windows now gets stored.

  ---
  Is the heartbeat injection for closed dwells actually needed?

  Yes — heartbeats for closed dwells are necessary. A dwell that closes at T+10min has an AppDwell(T+10min) but for context windows that fall entirely before T+10min (e.g. a window at [T+3min,
  T+4min] whose context ends at T+4min), the AppDwell is outside the context and can't be seen. The heartbeat at T+3min45s in context is what lets BuildOverlapSegments synthesize the open-dwell
  segment correctly.

  So the design is correct — it's the linear scan performance and the unclosed-dwell gap scenario that are the actual problems.

  ---
  The two fixes

  Fix 1 — Replace linear scan with sorted range slicing (FeatureExtractorService.cs:425):

  // Replace this:
  var context = allSignals
      .Where(e => e.TimestampUtc >= historyStart && e.TimestampUtc < window.EndUtc)
      .Where(e => ShouldIncludeSignal(e, profile))
      .ToList();

  // With this (allSignals is already sorted):
  var startIdx = allSignals.LowerBound(historyStart);   // binary search
  var endIdx   = allSignals.LowerBound(window.EndUtc);  // binary search
  var context  = allSignals
      .GetRange(startIdx, endIdx - startIdx)
      .Where(e => ShouldIncludeSignal(e, profile))
      .ToList();

  (Or use List.FindIndex / a helper that does binary search by timestamp.) This turns the scan from O(N_total) to O(log N + N_context) per window.

  Fix 2 — Cap heartbeat generation at a max gap threshold (AppDwellReplayPreprocessor.cs):

  If the time between a ForegroundAppChanged and the next event exceeds a reasonable threshold (e.g. WindowSizeSec + SlideSec = 90s, or something like 5 min), don't generate heartbeats across
  the entire gap. An unclosed dwell spanning hours is most likely a collection gap, not real sustained focus. The heartbeats are wasted there (and bloat the db with rows for a period the user
  wasn't even at the machine).

  Do you want me to implement either or both of these?