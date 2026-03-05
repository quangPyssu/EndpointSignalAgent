using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

/// <summary>
/// Presence/availability features sourced only from SessionStateCollector signals.
/// </summary>
internal sealed class SessionFeatureAggregator
{
    public SessionFeatureResult ExtractFeatures(
        IReadOnlyList<FeatureSignal> events,
        SlidingWindow window)
    {
        var features = FeatureSchema.SessionColumns.ToDictionary(column => column, _ => 0.0, StringComparer.Ordinal);

        var sessionEvents = events
            .Where(IsSessionSignal)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        var inWindow = sessionEvents
            .Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc)
            .ToList();

        features["lock_count"] = inWindow.Count(e => e.Type == SignalEventType.SessionLock);
        features["unlock_count"] = inWindow.Count(e => e.Type == SignalEventType.SessionUnlock);
        features["idle_api_fail_count"] = inWindow.Count(e =>
            e.Type == SignalEventType.IdleSample &&
            string.Equals(PayloadValueReader.GetString(e.Payload, "idleStatus"), "api_fail", StringComparison.OrdinalIgnoreCase));

        var state = BuildInitialState(sessionEvents, window.StartUtc);
        var intervals = new List<SessionInterval>();

        DateTimeOffset cursor = window.StartUtc;
        var ordered = sessionEvents.Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc).ToList();

        var displayToggleCount = 0;
        var screensaverToggleCount = 0;

        foreach (var evt in ordered)
        {
            if (evt.TimestampUtc > cursor)
            {
                intervals.Add(CreateInterval(cursor, evt.TimestampUtc, state));
                cursor = evt.TimestampUtc;
            }

            var previousDisplay = state.DisplayState;
            var previousScreenSaver = state.ScreenSaverOn;

            ApplyStateTransition(evt, ref state);

            if (evt.Type is SignalEventType.DisplayOn or SignalEventType.DisplayOff or SignalEventType.DisplayDimmed)
            {
                if (previousDisplay != state.DisplayState)
                {
                    displayToggleCount++;
                }
            }

            if (evt.Type is SignalEventType.ScreenSaverOn or SignalEventType.ScreenSaverOff)
            {
                if (previousScreenSaver != state.ScreenSaverOn)
                {
                    screensaverToggleCount++;
                }
            }
        }

        if (cursor < window.EndUtc)
        {
            intervals.Add(CreateInterval(cursor, window.EndUtc, state));
        }

        var windowMs = Math.Max(1L, window.DurationMs);

        var lockedMs = intervals.Where(i => i.Locked).Sum(i => i.DurationMs);
        var displayOffMs = intervals.Where(i => i.DisplayState == SessionDisplayState.Off).Sum(i => i.DurationMs);
        var displayDimMs = intervals.Where(i => i.DisplayState == SessionDisplayState.Dimmed).Sum(i => i.DurationMs);
        var displayOnMs = intervals.Where(i => i.DisplayState == SessionDisplayState.On).Sum(i => i.DurationMs);
        var screenSaverMs = intervals.Where(i => i.ScreenSaverOn).Sum(i => i.DurationMs);

        var presenceKnownMs = intervals.Where(i => i.PresenceKnown).Sum(i => i.DurationMs);
        var presenceAwayMs = intervals.Where(i => i.PresenceKnown && i.PresenceAway).Sum(i => i.DurationMs);
        var presencePresentMs = intervals.Where(i => i.PresenceKnown && !i.PresenceAway).Sum(i => i.DurationMs);

        var idleKnownIntervals = intervals.Where(i => i.IdleKnown).ToList();
        var idleKnownMs = idleKnownIntervals.Sum(i => i.DurationMs);
        var idleWeighted = idleKnownIntervals.Sum(i => (double)i.IdleBucketSec * i.DurationMs);
        var idleMax = idleKnownIntervals.Select(i => i.IdleBucketSec).DefaultIfEmpty(0).Max();
        var idleGe60Ms = idleKnownIntervals.Where(i => i.IdleBucketSec >= 60).Sum(i => i.DurationMs);
        var idleGe300Ms = idleKnownIntervals.Where(i => i.IdleBucketSec >= 300).Sum(i => i.DurationMs);

        features["display_toggle_count"] = displayToggleCount;
        features["screensaver_toggle_count"] = screensaverToggleCount;
        features["locked_ratio"] = FeatureMath.SafeDivide(lockedMs, windowMs);
        features["display_off_ratio"] = FeatureMath.SafeDivide(displayOffMs, windowMs);
        features["display_dim_ratio"] = FeatureMath.SafeDivide(displayDimMs, windowMs);
        features["display_on_ratio"] = FeatureMath.SafeDivide(displayOnMs, windowMs);
        features["screensaver_on_ratio"] = FeatureMath.SafeDivide(screenSaverMs, windowMs);
        features["presence_away_ratio"] = FeatureMath.SafeDivide(presenceAwayMs, windowMs);
        features["presence_present_ratio"] = FeatureMath.SafeDivide(presencePresentMs, windowMs);
        features["presence_available_ratio"] = FeatureMath.SafeDivide(presenceKnownMs, windowMs);
        features["idle_bucket_mean_sec"] = idleKnownMs > 0 ? idleWeighted / idleKnownMs : 0.0;
        features["idle_bucket_max_sec"] = idleMax;
        features["idle_ge_60_ratio"] = FeatureMath.SafeDivide(idleGe60Ms, windowMs);
        features["idle_ge_300_ratio"] = FeatureMath.SafeDivide(idleGe300Ms, windowMs);
        features["has_idle_data"] = idleKnownMs > 0 ? 1.0 : 0.0;
        features["has_display_data"] = (displayOffMs + displayDimMs + displayOnMs) > 0 ? 1.0 : 0.0;

        return new SessionFeatureResult(features, intervals);
    }

    private static bool IsSessionSignal(FeatureSignal signal)
    {
        return signal.Type is SignalEventType.SessionLock or
            SignalEventType.SessionUnlock or
            SignalEventType.DisplayOn or
            SignalEventType.DisplayOff or
            SignalEventType.DisplayDimmed or
            SignalEventType.ScreenSaverOn or
            SignalEventType.ScreenSaverOff or
            SignalEventType.IdleSample;
    }

    private static SessionState BuildInitialState(IReadOnlyList<FeatureSignal> orderedEvents, DateTimeOffset windowStart)
    {
        var state = SessionState.Default;
        foreach (var evt in orderedEvents)
        {
            if (evt.TimestampUtc >= windowStart)
            {
                break;
            }

            ApplyStateTransition(evt, ref state);
        }

        return state;
    }

    private static SessionInterval CreateInterval(DateTimeOffset startUtc, DateTimeOffset endUtc, SessionState state)
    {
        return new SessionInterval(
            startUtc,
            endUtc,
            state.Locked,
            state.DisplayState,
            state.ScreenSaverOn,
            state.PresenceKnown,
            state.PresenceAway,
            state.IdleKnown,
            state.IdleBucketSec);
    }

    private static void ApplyStateTransition(FeatureSignal evt, ref SessionState state)
    {
        switch (evt.Type)
        {
            case SignalEventType.SessionLock:
                state.Locked = true;
                break;

            case SignalEventType.SessionUnlock:
                state.Locked = false;
                break;

            case SignalEventType.DisplayOn:
                state.DisplayState = SessionDisplayState.On;
                UpdatePresenceFromPayload(evt.Payload, ref state);
                break;

            case SignalEventType.DisplayOff:
                state.DisplayState = SessionDisplayState.Off;
                UpdatePresenceFromPayload(evt.Payload, ref state);
                break;

            case SignalEventType.DisplayDimmed:
                state.DisplayState = SessionDisplayState.Dimmed;
                UpdatePresenceFromPayload(evt.Payload, ref state);
                break;

            case SignalEventType.ScreenSaverOn:
                state.ScreenSaverOn = true;
                break;

            case SignalEventType.ScreenSaverOff:
                state.ScreenSaverOn = false;
                break;

            case SignalEventType.IdleSample:
                if (PayloadValueReader.TryGetInt(evt.Payload, "idleBucketSec", out var idleBucketSec) && idleBucketSec >= 0)
                {
                    state.IdleKnown = true;
                    state.IdleBucketSec = idleBucketSec;
                }

                UpdatePresenceFromPayload(evt.Payload, ref state);
                break;
        }
    }

    private static void UpdatePresenceFromPayload(IReadOnlyDictionary<string, string> payload, ref SessionState state)
    {
        if (!payload.TryGetValue("userPresence", out var presenceRaw) || string.IsNullOrWhiteSpace(presenceRaw))
        {
            return;
        }

        var normalized = presenceRaw.Trim().ToLowerInvariant();
        if (normalized is "away")
        {
            state.PresenceKnown = true;
            state.PresenceAway = true;
            return;
        }

        if (normalized is "present")
        {
            state.PresenceKnown = true;
            state.PresenceAway = false;
        }
    }

    private struct SessionState
    {
        public bool Locked;
        public SessionDisplayState DisplayState;
        public bool ScreenSaverOn;
        public bool PresenceKnown;
        public bool PresenceAway;
        public bool IdleKnown;
        public int IdleBucketSec;

        public static SessionState Default => new()
        {
            Locked = false,
            DisplayState = SessionDisplayState.Unknown,
            ScreenSaverOn = false,
            PresenceKnown = false,
            PresenceAway = false,
            IdleKnown = false,
            IdleBucketSec = 0
        };
    }
}

