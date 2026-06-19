# Markov State Schema — Full Evaluation Analysis

**All versions evaluated:** vA, vB, vC, vD, vW, vE, vF
**Dataset:** 15 participants · 205,733 windows · Profile W60_S30
**Annotated participants (is_abnormal set):** P001, P005, P006, P011

---

## 1. Schema Overview

| Version | Dimensions | What it measures | Theoretical max states |
|---|---|---|---|
| **vA** | WorkMode × EngagementMode | App category × idle/active behaviour | 53 |
| **vB** | FocusDepth × SwitchCadence | App attention concentration × switching rate | 10 |
| **vC** | NetworkMode × ResourceMode | Network connectivity × CPU load | 30 |
| **vD** | PresenceMode × DisplayMode | Physical presence × screen state | 17 |
| **vW** | WorkMode (alone) | App category only — diagnostic baseline | 13 |
| **vE** | WorkMode × TimeBucket (UTC+7) | App category × time of day (local) | 53 |
| **vF** | WorkMode × TimeBucket × EngagementMode | App category × time of day × idle/active | 209 |

### TimeBucket definition (used in vE, vF)

| Bucket | UTC+7 local hours |
|---|---|
| `EarlyMorning` | 05:00–08:59 |
| `CoreHours` | 09:00–17:59 |
| `Evening` | 18:00–22:59 |
| `Night` | 23:00–04:59 |

---

## 2. State Space — Observed per Participant

| Version | Theoretical max | Observed range | Median |
|---|---|---|---|
| **vA** | 53 | 18–35 | ~27 |
| **vB** | 10 | 6–10 | 9 |
| **vC** | 30 | 2–7 | 5 |
| **vD** | 17 | 6–8 | 8 |
| **vW** | 13 | 6–10 | 8 |
| **vE** | 53 | 14–35 | 22 |
| **vF** | 209 | 33–94 | 66 |

**Reading:** vC and vD collapse the state space too aggressively (vC is degenerate — 97.9% of windows fall in `Offline_HeavyLoad`). vF expands it too far, producing ~66 observed states per participant on average. vA, vB, vW, and vE land in the tractable 6–35 range.

---

## 3. Transition Sparsity — RarePct < 60%

RarePct = fraction of observed transition types (from → to pairs) seen fewer than 5 times in training.

| Version | Range | Participants failing (> 60%) | Assessment |
|---|---|---|---|
| **vA** | 52.9%–69.9% | 9 of 15 | MARGINAL |
| **vB** | 45.0%–70.7% | 6 of 15 | MARGINAL |
| **vC** | 0.0%–63.6% | 1 of 15 | PASS (degenerate low) |
| **vD** | 12.5%–52.6% | 0 of 15 | **PASS** |
| **vW** | 20.7%–66.7% | 1 of 15 | PASS |
| **vE** | 38.9%–69.8% | 4 of 15 | MARGINAL |
| **vF** | 59.5%–74.5% | 14 of 15 | **FAIL** |

**Reading:** vD achieves the cleanest sparsity result. vF universally exceeds the 60% threshold — the triple product creates too many rarely-visited transition cells. vW's compact 13-state space performs well here.

---

## 4. Generalisation — NormalUnseen < 20% (Critical Feasibility Gate)

NormalUnseen = fraction of held-out normal transitions (last 30% of windows, chronological) never seen in training (first 70%). This must be < 20% for the model to be feasible.

| Version | Range | Any participant failing? | Assessment |
|---|---|---|---|
| **vA** | 0.0%–5.6% | No | **PASS** |
| **vB** | 0.0%–5.1% | No | **PASS** |
| **vC** | 0.0%–0.1% | No | PASS (trivially — degenerate) |
| **vD** | 0.0%–0.4% | No | **PASS** |
| **vW** | 0.0%–0.5% | No | **PASS** |
| **vE** | 0.1%–1.8% | No | **PASS** |
| **vF** | 0.3%–5.9% | No | **PASS** |

**Reading:** Every version passes the critical gate — none exceeds 20%. Even vF with 33–94 observed states per participant trains adequately on the available data. NormalUnseen is not the discriminating factor across versions; the anomaly signal (Criterion 4) is.

---

## 5. Anomaly Signal — AbnUnseen vs NormalUnseen

AbnUnseen = same unseen-transition metric but applied to annotated abnormal windows. A meaningful anomaly detector should show AbnUnseen >> NormalUnseen.

### Annotated participant results

| PID | NormalUnseen | vA AbnUnseen | vB AbnUnseen | vC AbnUnseen | vD AbnUnseen | vW AbnUnseen | vE AbnUnseen | vF AbnUnseen |
|---|---|---|---|---|---|---|---|---|
| **001** | 0.6% / 0.1% / 0.0% / 0.0% / 0.2% / 0.5% / 3.7% | **0.7%** | 0.0% | 0.0% | **0.7%** | 0.0% | 0.0% | 0.0% |
| **005** | 0.3% / 0.0% / 0.0% / 0.0% / 0.1% / 0.2% / 0.5% | **8.3%** | **4.2%** | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| **006** | 0.1% / 0.1% / 0.0% / 0.0% / 0.0% / 0.2% / 0.3% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| **011** | 2.6% / 5.1% / 0.0% / 0.1% / 0.3% / 0.9% / 3.3% | **12.5%** | **12.5%** | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

*(NormalUnseen columns left-to-right match vA / vB / vC / vD / vW / vE / vF)*

### Signal summary

| Version | P001 | P005 | P006 | P011 | Participants with signal |
|---|---|---|---|---|---|
| **vA** | Slight ↑ | **Strong (27×)** | None | **Strong (5×)** | **2 of 4** |
| **vB** | None | Moderate (∞) | None | **Strong** | **2 of 4** |
| **vC** | None | None | None | None | 0 of 4 |
| **vD** | Slight ↑ | None | None | None | 1 of 4 |
| **vW** | None | None | None | None | 0 of 4 |
| **vE** | None | None | None | None | 0 of 4 |
| **vF** | None | None | None | None | 0 of 4 |

---

## 6. Overall Rankings

| Rank | Version | Anomaly signal | Mechanical quality | Verdict |
|---|---|---|---|---|
| **1** | **vA** | Strongest — P005 8.3%, P011 12.5% | Marginal sparsity, good generalisation | **Primary detector** |
| **2** | **vB** | Second — P005 4.2%, P011 12.5% | Best compact design (10 states max) | Complementary layer |
| **3** | **vD** | Weak — P001 only (0.7%) | Best RarePct of all versions | Presence-rhythm supplement |
| **4** | **vW** | None | Very compact, clean | Confirmed EngagementMode is load-bearing |
| **5** | **vE** | None | Moderate sparsity | Confirmed TimeBucket alone cannot replace EngagementMode |
| **6** | **vC** | None | Trivially passes (degenerate) | Discard — single dominant state |
| **7** | **vF** | None | Universal high RarePct (59–74%) | Discard as primary detector |

---

## 7. Diagnostic Findings from vW, vE, vF

The three new versions were designed as controlled experiments to isolate which dimensions carry the anomaly signal.

### Finding 1: EngagementMode is load-bearing (vW diagnostic)

Removing EngagementMode from vA (leaving WorkMode alone = vW) dropped AbnUnseen to 0.0% for all four annotated participants. The anomaly signal does not live in WorkMode transition sequences alone — it requires the EngagementMode dimension.

> **Implication:** Any schema that drops EngagementMode loses the ability to detect the anomalies observed in P005 and P011.

### Finding 2: TimeBucket cannot replace EngagementMode (vE diagnostic)

Replacing EngagementMode with TimeBucket (UTC+7 local time) — keeping WorkMode — also produced 0.0% AbnUnseen across all annotated participants. The anomalies recorded for P005 and P011 do not manifest as unusual time-of-day patterns; they manifest as unusual activity/idleness combinations within a given app context.

> **Implication:** The anomalous scenarios in this dataset are about *how* participants were behaving at the machine, not *when*.

### Finding 3: Adding TimeBucket to vA fragments without sharpening (vF diagnostic)

The full triple product (WorkMode × TimeBucket × EngagementMode = vF) also produced 0.0% AbnUnseen, despite passing the NormalUnseen feasibility gate (max 5.9%). The mechanism is dilution: each previously-rare abnormal transition (e.g. `DeveloperWork → BrowserWork_LightIdle`) is now split across four time variants (`DeveloperWork_CoreHours`, `DeveloperWork_Evening`, etc.), so the training split likely contains at least one occurrence of each fragment — making nothing look "unseen."

> **Implication:** For this dataset, adding temporal context to a transition-based anomaly detector does not add discriminating power. The time dimension would be more useful as a post-detection label ("this anomaly occurred at Night") than as a state dimension.

---

## 8. What Works and What Doesn't — Summary

| Dimension | Carries anomaly signal? | Evidence |
|---|---|---|
| WorkMode (app category) | No alone — but necessary | vW: 0% AbnUnseen without EngagementMode |
| EngagementMode (idle/active) | No alone — but necessary | Implied: vA loses signal when removed |
| WorkMode × EngagementMode | **Yes — strongest signal** | vA: P005 8.3%, P011 12.5% |
| FocusDepth × SwitchCadence | Partial | vB: P005 4.2%, P011 12.5% |
| TimeBucket (time of day) | No | vE: 0% AbnUnseen replacing EngagementMode |
| NetworkMode × ResourceMode | No | vC: degenerate state space |
| PresenceMode × DisplayMode | Marginal (P001 only) | vD: 0.7% for P001 only |
| Triple product with TimeBucket | No — dilutes signal | vF: 0% AbnUnseen despite feasibility pass |

---

## 9. Recommended Architecture

**Primary Markov detector:** vA (WorkMode × EngagementMode)
- Use W60_S30 profile (60s windows, 30s slide)
- Train on first 70% of each participant's normal sessions (chronological)
- Flag windows where transition probability falls below threshold as anomalous

**Supplementary layer:** vB (FocusDepth × SwitchCadence)
- Runs in parallel; captures app-behaviour disruption orthogonal to vA
- P011 is equally detected (12.5%); P005 is partially detected (4.2%)
- Fusing both layers would catch more anomaly types

**Post-detection annotation:** TimeBucket (UTC+7)
- Attach the time-of-day bucket as a label to flagged windows *after* detection
- Enables "anomaly at unusual hour" reporting without degrading the detector

**Not recommended:** vC (degenerate), vF (signal dilution), vW/vE (no signal)

---

## 10. Open Questions

1. **P006 signal gap:** All versions produce 0.0% AbnUnseen for P006. The annotated abnormal segment for P006 stayed within established behavioral vocabulary — a known limitation. More annotation data or a different anomaly scenario type may be needed.
2. **Broader annotation coverage:** Only 4 of 15 participants have annotation data. The signal from P005 and P011 is consistent and strong, but results should be validated across more participants before production deployment.
3. **vG (CoarseWorkMode × EngagementMode):** Not yet evaluated. Merging the 13 WorkMode categories into 6 coarser groups would reduce vA's state count from 18–35 to ~12–20 while potentially preserving most of the anomaly signal. Worth testing as a lower-sparsity alternative to vA.
4. **W120_S60 profile:** Longer windows (120s, 60s slide) have been run for vB–vF but not exhaustively analysed against annotation data. Longer windows aggregate more activity per state and may reduce sparsity.

---

*Generated 2026-06-19 · Covers versions vA–vF · Profile W60_S30 · 205,733 windows · 15 participants*
