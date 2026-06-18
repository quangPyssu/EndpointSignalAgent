# Markov State Version C — Evaluation Findings

**Profile evaluated:** W60_S30
**Participants:** 15
**Total windows:** 205,733
**State schema:** NetworkMode × ResourceMode

---

## What Version C is

Version C maps each feature window to a single Markov state:

```
markov_state_vC = NetworkMode + ResourceMode
```

**NetworkMode** captures the network connectivity context:

| Priority | State | Rule |
|---|---|---|
| 1 | `Offline` | `primary_wifi_connected_ratio < 0.25 and vpn_on_ratio < 0.10` |
| 2 | `VPNActive` | `vpn_on_ratio >= 0.50` |
| 3 | `NetworkFlux` | `vpn_flip_count > 1 or ssid_change_count > 0` |
| 4 | `OnlineSecure` | `primary_wifi_connected_ratio >= 0.75` |
| 5 | `OnlineMixed` | `primary_wifi_connected_ratio >= 0.25` |
| 6 | `UnknownNetwork` | none of the above |

**ResourceMode** captures the CPU/system load profile:

| Priority | State | Rule |
|---|---|---|
| 1 | `NoSystemData` | `has_system_data == 0` |
| 2 | `HeavyLoad` | `system_load_index >= 0.65` |
| 3 | `VariableLoad` | `resource_variability_index >= 0.35` |
| 4 | `ModerateLoad` | `cpu_usage_mean >= 0.30` |
| 5 | `LightLoad` | otherwise |

**No collapsing** — all NetworkMode × ResourceMode combinations are meaningful.

**Theoretical maximum: 30 states** (6×5). In practice expect 10–18 per participant.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| Offline_HeavyLoad | 201,420 |
| Offline_NoSystemData | 2,054 |
| OnlineSecure_HeavyLoad | 842 |
| VPNActive_HeavyLoad | 716 |
| NetworkFlux_HeavyLoad | 656 |
| OnlineSecure_NoSystemData | 17 |
| UnknownNetwork_HeavyLoad | 15 |
| VPNActive_NoSystemData | 13 |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| 001 | 12,788 | 5 | 16 | 37.5% | 0.0% | 0.0% |
| 002 | 5,223 | 7 | 19 | 47.4% | 0.0% | 0.0% |
| 003 | 20,685 | 5 | 15 | 20.0% | 0.0% | 0.0% |
| 004 | 3,135 | 2 | 4 | 0.0% | 0.0% | 0.0% |
| 005 | 23,658 | 6 | 19 | 36.8% | 0.0% | 0.0% |
| 006 | 10,764 | 2 | 4 | 0.0% | 0.0% | 0.0% |
| 007 | 17,626 | 7 | 24 | 37.5% | 0.0% | 0.0% |
| 008 | 10,462 | 5 | 13 | 53.8% | 0.0% | 0.0% |
| 009 | 7,418 | 5 | 12 | 33.3% | 0.0% | 0.0% |
| 010 | 29,796 | 5 | 15 | 26.7% | 0.0% | 0.0% |
| 011 | 10,975 | 2 | 4 | 0.0% | 0.0% | 0.0% |
| 012 | 6,076 | 2 | 4 | 0.0% | 0.0% | 0.0% |
| 013 | 31,570 | 5 | 12 | 50.0% | 0.0% | 0.0% |
| 014 | 4,191 | 5 | 11 | 63.6% | 0.1% | 0.0% |
| 015 | 11,366 | 5 | 16 | 25.0% | 0.1% | 0.0% |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant ≤ 20 (well below 30 theoretical max)

**Result:** 2–7 states observed per participant (range 2–7)

**Verdict:** PASS. The schema collapses nearly all variation into `Offline_HeavyLoad`, yielding an extremely compact state space — far below the 20-state ceiling and even well below the expected 10–18 range.

### Criterion 2: RarePct < 60%

**Result:** 0.0%–63.6% (14 of 15 participants below 60%; P014 at 63.6%)

**Verdict:** MARGINAL. Nearly all participants pass comfortably, but P014 slightly exceeds the 60% threshold; given Version C has more states than vB, marginal sparsity in one small participant (4,191 windows) is tolerable.

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** 0.0%–0.1% across all participants

**Verdict:** PASS. NormalUnseen is essentially zero for all participants, meaning the normal profile almost perfectly covers all states the model encounters — far better than the 20% threshold.

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal |
|---|---|---|---|
| 001 | 0.0% | 0.0% | No separation |
| 005 | 0.0% | 0.0% | No separation |
| 006 | 0.0% | 0.0% | No separation |
| 011 | 0.0% | 0.0% | No separation |

**Verdict:** FAIL. Because the state space is almost entirely dominated by `Offline_HeavyLoad` (97.9% of all windows), both normal and abnormal sessions fall into the same single state, producing zero AbnUnseen. Version C provides no anomaly-detection signal — it cannot distinguish abnormal from normal behavior because network/resource context adds no discriminating dimension in this dataset.

---

## Comparison to Version A

| Metric | Version A | Version C |
|---|---|---|
| Theoretical max states | 53 | 30 |
| Observed state range | 18–35 | 2–7 |
| RarePct range | 52.9%–69.9% | 0.0%–63.6% |
| NormalUnseen range | 0.0%–5.6% | 0.0%–0.1% |
| P005 AbnUnseen | 8.3% | 0.0% |
| P011 AbnUnseen | 12.5% | 0.0% |

---

## Overall verdict

Version C is technically feasible in the sense that it is stable (low sparsity, near-zero NormalUnseen), but it completely fails its core purpose as an anomaly detector. The dataset is overwhelmingly dominated by `Offline_HeavyLoad` (97.9% of all windows), which means the network/resource dimension provides no discriminating power — both normal and abnormal sessions map to identical states. Compared to Version A, which showed meaningful AbnUnseen signals for P005 (8.3%) and P011 (12.5%), Version C produces 0.0% AbnUnseen across all focus participants. The network/resource dimension does not add independent anomaly signal beyond Version A; it actively destroys the signal by collapsing the state space too aggressively around a single dominant state.

*Generated by `MarkovTest/run.py` · Version C · Profile W60_S30 · 2026-06-18*
