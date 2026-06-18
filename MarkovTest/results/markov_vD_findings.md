# Markov State Version D — Evaluation Findings

**Profile evaluated:** W60_S30
**Participants:** 15
**Total windows:** 205,733
**State schema:** PresenceMode × DisplayMode

---

## What Version D is

Version D maps each feature window to a single Markov state:

```
markov_state_vD = PresenceMode + DisplayMode
```

**PresenceMode** captures physical user presence and lock status:

| Priority | State | Rule |
|---|---|---|
| 1 | `Locked` | `locked_ratio >= 0.80` |
| 2 | `ConfirmedPresent` | `presence_present_ratio >= 0.70` |
| 3 | `ProbablyAway` | `presence_away_ratio >= 0.70` |
| 4 | `AmbiguousPresence` | `presence_present_ratio > 0 or presence_away_ratio > 0` |
| 5 | `UnknownPresence` | both ratios = 0 |

**DisplayMode** captures screen on/off patterns:

| Priority | State | Rule |
|---|---|---|
| 1 | `DisplayAlwaysOn` | `display_on_ratio >= 0.85` |
| 2 | `DisplayMostlyOn` | `display_on_ratio >= 0.50` |
| 3 | `DisplayOff` | `display_off_ratio >= 0.60` |
| 4 | `DisplayMixed` | none of the above |

**Collapsing rule:** If `PresenceMode == Locked` → state is just `Locked`.

**Theoretical maximum: 17 states** (1 + 4×4). In practice expect 8–14 per participant depending on whether the presence sensor was active.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| ConfirmedPresent_DisplayMixed | 173,675 |
| UnknownPresence_DisplayMixed | 22,197 |
| AmbiguousPresence_DisplayMixed | 5,029 |
| ConfirmedPresent_DisplayAlwaysOn | 2,252 |
| ConfirmedPresent_DisplayOff | 1,537 |
| ConfirmedPresent_DisplayMostlyOn | 676 |
| Locked | 191 |
| AmbiguousPresence_DisplayMostlyOn | 172 |
| UnknownPresence_DisplayAlwaysOn | 3 |
| UnknownPresence_DisplayMostlyOn | 1 |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| 001 | 12,788 | 8 | 31 | 48.4% | 0.0% | 0.7% |
| 002 | 5,223 | 8 | 31 | 38.7% | 0.4% | 0.0% |
| 003 | 20,685 | 8 | 24 | 29.2% | 0.0% | 0.0% |
| 004 | 3,135 | 6 | 16 | 12.5% | 0.0% | 0.0% |
| 005 | 23,658 | 8 | 27 | 33.3% | 0.0% | 0.0% |
| 006 | 10,764 | 6 | 16 | 12.5% | 0.0% | 0.0% |
| 007 | 17,626 | 8 | 31 | 38.7% | 0.1% | 0.0% |
| 008 | 10,462 | 7 | 19 | 21.1% | 0.0% | 0.0% |
| 009 | 7,418 | 8 | 31 | 29.0% | 0.0% | 0.0% |
| 010 | 29,796 | 8 | 30 | 30.0% | 0.0% | 0.0% |
| 011 | 10,975 | 8 | 34 | 20.6% | 0.1% | 0.0% |
| 012 | 6,076 | 8 | 30 | 16.7% | 0.1% | 0.0% |
| 013 | 31,570 | 7 | 19 | 52.6% | 0.0% | 0.0% |
| 014 | 4,191 | 7 | 18 | 38.9% | 0.2% | 0.0% |
| 015 | 11,366 | 8 | 19 | 42.1% | 0.1% | 0.0% |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant ≤ 14 (well below 17 theoretical max)

**Result:** 6–8 states per participant

**Verdict:** PASS. All participants fall well below the 14-state ceiling, using only 6–8 of the 17 theoretical states. The dominance of `ConfirmedPresent_DisplayMixed` (84.4% of all windows) and `UnknownPresence_DisplayMixed` explains the low variety. No collapse to a single dominant state (unlike vC), but `ConfirmedPresent_DisplayMixed` is very dominant overall — this is expected if most working sessions have confirmed presence with mixed display activity, not a pathological collapse.

### Criterion 2: RarePct < 60%

**Result:** Range 12.5%–52.6%

**Verdict:** PASS. All 15 participants are under the 60% threshold. This is a significant improvement over vA (52.9%–69.9%). The smaller, more semantically coherent state space means transitions concentrate better, leaving fewer rarely-visited cells.

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** Range 0.0%–0.4% (all participants)

**Verdict:** PASS. Extremely low NormalUnseen values across the board — the Markov model trained on normal sessions covers the normal test data nearly completely. This is the best NormalUnseen result seen across all versions evaluated so far.

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal |
|---|---|---|---|
| 001 | 0.0% | 0.7% | YES |
| 005 | 0.0% | 0.0% | NO |
| 006 | 0.0% | 0.0% | NO |
| 011 | 0.1% | 0.0% | NO |

**Verdict:** FAIL for most participants. Only P001 shows a positive anomaly signal (AbnUnseen 0.7% > NormalUnseen 0.0%). P005 and P006, which showed strong or moderate signals in vA (8.3% and 0.0% respectively), both register 0.0% AbnUnseen in vD. P011 has a marginally inverted signal (0.1% NormalUnseen vs 0.0% AbnUnseen). The physical-presence/display dimension does not reliably separate anomalous from normal behaviour for these participants — the abnormal sessions appear to follow the same ConfirmedPresent_DisplayMixed profile as normal sessions, leaving no visible footprint in vD's state space.

---

## Comparison to Version A

| Metric | Version A | Version D |
|---|---|---|
| Theoretical max states | 53 | 17 |
| Observed state range | 18–35 | 6–8 |
| RarePct range | 52.9%–69.9% | 12.5%–52.6% |
| NormalUnseen range | 0.0%–5.6% | 0.0%–0.4% |
| P005 AbnUnseen | 8.3% | 0.0% |
| P011 AbnUnseen | 12.5% | 0.0% |

---

## Overall verdict

Version D succeeds on the mechanical quality metrics — state space is compact (6–8 observed vs 17 theoretical), RarePct is comfortably under 60% for all participants, and NormalUnseen is near-zero everywhere, indicating the model generalises cleanly to normal test data. However, it fails the most critical criterion: it produces almost no anomaly signal. The abnormal sessions for P005 and P011 — which showed 8.3% and 12.5% AbnUnseen respectively under vA — register 0.0% AbnUnseen under vD, meaning physical presence/display state during the injected anomalies was indistinguishable from normal working behaviour. vD is feasible as a supplementary layer (it cleanly tracks user presence rhythms) but is not viable as a standalone anomaly detector; it captures a different behavioural dimension than vA rather than a better one.

*Generated by `MarkovTest/run.py` · Version D · Profile W60_S30 · 2026-06-18*
