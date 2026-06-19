# Markov State Version F — Evaluation Findings

**Profile evaluated:** W60_S30 (60-second windows, 30-second slide)
**Participants:** 15
**Total windows:** 205,733
**State schema:** WorkMode × TimeBucket (UTC+7) × EngagementMode

---

## What Version F is

Version F is the full triple-product schema combining all three behavioral dimensions: what the user is doing (WorkMode), when they are doing it (TimeBucket, UTC+7), and how actively they are engaged (EngagementMode). This is the maximum behavioral context version.

```
markov_state_vF = WorkMode + TimeBucket(UTC+7) + EngagementMode
```

**WorkMode** — same 13 states as Version A.

**TimeBucket** — same 4 UTC+7 time buckets as Version E (EarlyMorning / CoreHours / Evening / Night).

**EngagementMode** — same 5 states as Version A (Locked / LongIdle / LightIdle / Active / UnknownEngagement).

**Collapsing:** Locked → `"Locked"`; NoApp → `"NoApp_{TimeBucket}_{EngagementMode}"`; otherwise `"{WorkMode}_{TimeBucket}_{EngagementMode}"`.

**Theoretical maximum: 209 states.** In practice per participant: 33–94 observed states (most 3-way combinations don't occur). NormalUnseen may be elevated for small participants — this is the key risk being evaluated.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| OtherWork_CoreHours_LongIdle | 19,719 |
| BrowserWork_Evening_LightIdle | 15,779 |
| OtherWork_CoreHours_LightIdle | 15,195 |
| OtherWork_Night_LongIdle | 14,403 |
| BrowserWork_CoreHours_LightIdle | 12,715 |
| OtherWork_Evening_LongIdle | 11,665 |
| OtherWork_EarlyMorning_LongIdle | 9,600 |
| OtherWork_Evening_LightIdle | 8,318 |
| OtherWork_Evening_UnknownEngagement | 7,007 |
| DeveloperWork_CoreHours_LightIdle | 6,453 |
| BrowserWork_CoreHours_LongIdle | 5,662 |
| BrowserWork_Night_LightIdle | 5,410 |
| OtherWork_Night_UnknownEngagement | 4,778 |
| OtherWork_Night_LightIdle | 4,453 |
| DeveloperWork_CoreHours_LongIdle | 4,138 |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| 001 | 12,788 | 69 | 408 | 71.8% | 3.7% | 0.0% |
| 002 | 5,223 | 62 | 244 | 67.6% | 3.2% | 0.0% |
| 003 | 20,685 | 90 | 586 | 60.6% | 1.2% | 0.0% |
| 004 | 3,135 | 47 | 183 | 68.8% | 3.1% | 0.0% |
| 005 | 23,658 | 57 | 192 | 70.8% | 0.5% | 0.0% |
| 006 | 10,764 | 35 | 111 | 59.5% | 0.3% | 0.0% |
| 007 | 17,626 | 76 | 422 | 62.8% | 1.4% | 0.0% |
| 008 | 10,462 | 67 | 258 | 65.9% | 0.5% | 0.0% |
| 009 | 7,418 | 71 | 368 | 68.8% | 1.9% | 0.0% |
| 010 | 29,796 | 94 | 440 | 71.6% | 0.4% | 0.0% |
| 011 | 10,975 | 35 | 165 | 64.8% | 3.3% | 0.0% |
| 012 | 6,076 | 66 | 259 | 74.5% | 2.1% | 0.0% |
| 013 | 31,570 | 50 | 288 | 61.8% | 0.4% | 0.0% |
| 014 | 4,191 | 33 | 117 | 73.5% | 5.9% | 0.0% |
| 015 | 11,366 | 44 | 225 | 61.8% | 1.1% | 0.0% |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant

**Result:** 33–94 states per participant (median ~62).

**Verdict:** FAIL. The expected range was 25–60. Eight of 15 participants exceed 60 observed states, with P003 at 90 and P010 at 94. The triple product has produced a significant state expansion over vA (18–35) and vE (14–35). The larger state space is not fatal on its own, but it elevates RarePct and creates risk for NormalUnseen, particularly for smaller participants.

### Criterion 2: RarePct < 60%

**Result:** 59.5%–74.5%. Only two participants (P006 at 59.5%, P003 at 60.6%) are at or near the 60% threshold; 13 of 15 participants exceed it.

**Verdict:** FAIL. RarePct is uniformly higher than vA and vE due to the larger state space fragmenting the transition distribution. The triple product creates too many low-frequency states. The vA range was 52.9%–69.9%; vF pushes most participants into the 65%–75% range.

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** 0.3%–5.9% across all 15 participants (W60_S30). All participants are well under the 20% threshold.

**Verdict:** PASS. Despite the state-space expansion, NormalUnseen remains low for all participants. The training split adequately covers the triple-product states in the training portion. The hypothesized feasibility failure (NormalUnseen > 20%) did not materialize for W60_S30. Note: W120_S60 shows P012 at 12.4% and P004 at 7.9%, approaching but still within the threshold at the coarser profile.

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal | vs. vA | vs. vE |
|---|---|---|---|---|---|
| 001 | 3.7% | 0.0% | NO | vA: 0.7% | vE: 0.0% |
| 005 | 0.5% | 0.0% | NO | vA: 8.3% | vE: 0.0% |
| 006 | 0.3% | 0.0% | NO | vA: 0.0% | vE: 0.0% |
| 011 | 3.3% | 0.0% | NO | vA: 12.5% | vE: 0.0% |

**Verdict:** FAIL. AbnUnseen is 0.0% for all four annotated participants — identical to the vE result and worse than vA (P005: 8.3%, P011: 12.5%). Adding TimeBucket to the vA schema (WorkMode × EngagementMode) has completely eliminated the anomaly signal rather than preserving or enhancing it. The triple product appears to over-partition the state space in the abnormal windows, distributing the anomalous transitions across so many states that none qualifies as "unseen" relative to the expanded normal training distribution.

---

## Comparison to all versions

| Metric | vA | vE | vF |
|---|---|---|---|
| Theoretical max states | 53 | 53 | 209 |
| Observed state range | 18–35 | 14–35 | 33–94 |
| RarePct range | 52.9%–69.9% | 38.9%–69.8% | 59.5%–74.5% |
| NormalUnseen range | 0.0%–5.6% | 0.1%–1.8% | 0.3%–5.9% |
| P005 AbnUnseen | 8.3% | 0.0% | 0.0% |
| P011 AbnUnseen | 12.5% | 0.0% | 0.0% |

---

## Overall verdict

Version F is **not feasible as a replacement for Version A**. Adding TimeBucket to the vA schema destroys the anomaly signal: P005 AbnUnseen drops from 8.3% to 0.0% and P011 drops from 12.5% to 0.0%, matching vE's failure mode rather than vA's success. The NormalUnseen criterion passes (all participants below 20%), so the triple product does not create an outright training-gap failure — but it does not improve detection either.

The most likely mechanism is that the triple product sub-divides the abnormal-window state transitions into time-bucketed variants (e.g. `IdleHeavy_CoreHours_Active` instead of `IdleHeavy_Active`), and the training split happens to contain enough of those specific sub-states that none qualifies as "unseen." In effect, adding time context dilutes the anomaly signal rather than concentrating it.

**Recommendation:** Discard vF as a standalone schema. Return to vA (WorkMode × EngagementMode) as the production candidate — it is the only version with confirmed AbnUnseen > NormalUnseen for annotated anomalous participants. If time-of-day context is still desired for interpretability, consider using vA as the primary anomaly detector and adding TimeBucket as a secondary label post-detection rather than baking it into the Markov state.

*Generated by `MarkovTest/run.py` · Version F · Profile W60_S30 · 2026-06-19*
