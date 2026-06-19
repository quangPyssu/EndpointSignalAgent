# Markov State Version E — Evaluation Findings

**Profile evaluated:** W60_S30 (60-second windows, 30-second slide)
**Participants:** 15
**Total windows:** 205,733
**State schema:** WorkMode × TimeBucket (UTC+7)

---

## What Version E is

Version E extends Version A's WorkMode with a temporal dimension — the local time of day for participants (UTC+7). The hypothesis is that anomalous events at unusual hours (e.g. DeveloperWork at Night) will produce unseen transitions not present in normal training data.

```
markov_state_vE = WorkMode + TimeBucket(UTC+7)
```

**WorkMode** — same 13 states as Version A.

**TimeBucket** — local UTC+7 hour binned into 4 named periods:

| Bucket | UTC+7 local hours |
|---|---|
| `EarlyMorning` | 05:00–08:59 |
| `CoreHours` | 09:00–17:59 |
| `Evening` | 18:00–22:59 |
| `Night` | 23:00–04:59 |

**Collapsing:** `Locked` short-circuits to `"Locked"` (no time context needed for lock windows).

**Theoretical maximum: 53 states** (13 WorkMode × 4 TimeBucket + 1 Locked). In practice expect 15–28 per participant.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| OtherWork_CoreHours | 38,722 |
| OtherWork_Evening | 27,116 |
| OtherWork_Night | 23,660 |
| BrowserWork_Evening | 21,667 |
| BrowserWork_CoreHours | 20,188 |
| OtherWork_EarlyMorning | 11,705 |
| DeveloperWork_CoreHours | 11,298 |
| BrowserWork_Night | 10,241 |
| SystemWork_CoreHours | 6,216 |
| DeveloperWork_Evening | 3,996 |
| CommsWork_CoreHours | 3,088 |
| SystemWork_Evening | 2,768 |
| MixedWork_Night | 2,719 |
| BrowserWork_EarlyMorning | 2,685 |
| SystemWork_Night | 2,401 |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| 001 | 12,788 | 25 | 134 | 45.5% | 0.5% | 0.0% |
| 002 | 5,223 | 21 | 89 | 47.2% | 1.8% | 0.0% |
| 003 | 20,685 | 35 | 211 | 38.9% | 0.1% | 0.0% |
| 004 | 3,135 | 15 | 66 | 53.0% | 1.4% | 0.0% |
| 005 | 23,658 | 25 | 96 | 69.8% | 0.2% | 0.0% |
| 006 | 10,764 | 15 | 49 | 53.1% | 0.2% | 0.0% |
| 007 | 17,626 | 26 | 138 | 52.9% | 0.4% | 0.0% |
| 008 | 10,462 | 25 | 98 | 51.0% | 0.1% | 0.0% |
| 009 | 7,418 | 27 | 131 | 55.0% | 0.4% | 0.0% |
| 010 | 29,796 | 33 | 193 | 57.0% | 0.1% | 0.0% |
| 011 | 10,975 | 14 | 60 | 56.7% | 0.9% | 0.0% |
| 012 | 6,076 | 23 | 96 | 69.8% | 1.2% | 0.0% |
| 013 | 31,570 | 18 | 101 | 44.5% | 0.2% | 0.0% |
| 014 | 4,191 | 15 | 60 | 61.7% | 0.7% | 0.0% |
| 015 | 11,366 | 15 | 80 | 45.0% | 0.3% | 0.0% |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant

**Result:** 14–35 states per participant (median ~23).

**Verdict:** MARGINAL. The expected range was 15–28; three participants (P003 at 35, P010 at 33, P009 at 27) are at or above the upper bound, meaning those participants actively used multiple WorkModes across all four time buckets. This is consistent with overnight or cross-timezone study data rather than a schema defect.

### Criterion 2: RarePct < 60%

**Result:** 38.9%–69.8%. Eleven of 15 participants are below 60%; P005 (69.8%), P012 (69.8%), and P014 (61.7%) exceed the threshold.

**Verdict:** MARGINAL. RarePct is notably lower than vA for many participants (vA range: 52.9%–69.9%), suggesting the time dimension distributes transitions more evenly and reduces the tail of extremely rare states. Participants with high RarePct (P005, P012) have highly concentrated activity in a few time slots.

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** 0.1%–1.8% across all 15 participants.

**Verdict:** PASS. All participants are well under the 20% threshold. The time dimension did not create significant training gaps — the training split apparently covers all four time buckets adequately for each WorkMode in use.

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal | vs. vA AbnUnseen |
|---|---|---|---|---|
| 001 | 0.5% | 0.0% | NO | vA: 0.7% |
| 005 | 0.2% | 0.0% | NO | vA: 8.3% |
| 006 | 0.2% | 0.0% | NO | vA: 0.0% |
| 011 | 0.9% | 0.0% | NO | vA: 12.5% |

**Verdict:** FAIL. AbnUnseen is 0.0% for all four annotated participants. This exactly mirrors the vW result (which also dropped to 0.0% when EngagementMode was removed). Adding TimeBucket in place of EngagementMode did not restore the anomaly signal. The anomalies in P005 and P011 do not coincide with unusual time-of-day patterns — they are captured by EngagementMode transitions, not time-of-day transitions.

---

## Comparison to Version A

| Metric | Version A | Version E |
|---|---|---|
| Theoretical max states | 53 | 53 |
| Observed state range | 18–35 | 14–35 |
| RarePct range | 52.9%–69.9% | 38.9%–69.8% |
| NormalUnseen range | 0.0%–5.6% | 0.1%–1.8% |
| P005 AbnUnseen | 8.3% | 0.0% |
| P011 AbnUnseen | 12.5% | 0.0% |

---

## Overall verdict

Version E **degrades the anomaly signal vs. vA**. Replacing EngagementMode with TimeBucket (UTC+7) eliminates the AbnUnseen signal entirely for P005 and P011 — the two participants with the strongest anomaly signal in vA. This confirms the conclusion from vW: EngagementMode is the load-bearing dimension for anomaly detection in this dataset.

TimeBucket does provide some benefits: RarePct is generally lower (better coverage), and NormalUnseen remains low (no training-gap issue). However, none of these advantages matter if the anomaly signal is zero.

**Implication for vF (WorkMode × EngagementMode × TimeBucket triple product):** The triple product is still worth evaluating. The rationale is that EngagementMode restores the anomaly signal lost by vE, while TimeBucket may add incremental discrimination for edge cases (e.g., unusual-hour anomalies that EngagementMode alone misses). However, the state-space explosion risk is real: with 13 WorkModes × 5 EngagementModes × 4 TimeBuckets + 1 Locked = 261 theoretical states, practical coverage per participant will be very sparse, and RarePct and NormalUnseen could spike dramatically. vF should only be pursued if the goal is maximum sensitivity and there is enough training data to cover the expanded state space.

*Generated by `MarkovTest/run.py` · Version E · Profile W60_S30 · 2026-06-19*
