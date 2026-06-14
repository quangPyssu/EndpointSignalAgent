# Heartbeat Injection Analysis — Participants 1–10

**Date:** 2026-06-13  
**Branch:** Data_Recovery  
**Scope:** Per-participant comparison of `features.db` (old, pre-injection) vs `participant_PXXX/features.db` (new, post-injection)  
**Metrics compared:** triple-zero source counts, `has_app_data`, `app_dwell_mean_ms` with app data present

---

## Summary Table

| Participant | Date Range | Total Rows (old→new) | Triple-Zero Fixed | `has_app_data=0` old | `has_app_data=0` new | `dwell_mean=0` (app=1) Fixed |
|---|---|---|---|---|---|---|
| P001 | 2026-04-24 → 2026-05-18 | 44 435 → 44 659 (+224) | 11 211 / 11 214 | 22 643 (51.0%) | 19 (0.0%) | 15 067 / 15 098 |
| P002 | 2026-05-06 → 2026-05-26 | 18 033 → 18 163 (+130) | 1 384 / 1 439  | 9 211 (51.1%) | 506 (2.8%) | 5 659 / 5 876 |
| P003 | 2026-05-03 → 2026-05-23 | 72 182 → 72 332 (+150) | 1 324 / 1 324  | 27 859 (38.6%) | 6 (0.0%) | 15 899 / 15 921 |
| N004 | 2026-05-07 → 2026-06-07 | 10 821 → 10 933 (+112) | 853 / 853      | 4 807 (44.4%) | 0 (0.0%) | 2 931 / 2 954 |
| P005 | 2026-05-05 → 2026-05-31 | 82 737 → 82 784 (+47)  | 389 / 392      | 47 031 (56.8%) | 11 (0.0%) | 34 565 / 34 586 |
| P006 | 2026-04-30 → 2026-05-19 | 37 024 → 37 502 (+478) | 169 / 169      | 20 532 (55.5%) | 6 (0.0%) | 14 287 / 14 423 |
| P007 | 2026-05-03 → 2026-05-20 | 61 049 → 61 501 (+452) | 4 344 / 4 344  | 29 579 (48.5%) | 56 (0.1%) | 18 461 / 18 609 |
| P008 | 2026-05-04 → 2026-05-23 | 36 507 → 36 563 (+56)  | 3 073 / 3 073  | 17 926 (49.1%) | 39 (0.1%) | 11 691 / 11 806 |
| P009 | 2026-05-06 → 2026-05-27 | 25 603 → 25 859 (+256) | 1 571 / 1 573  | 11 685 (45.6%) | 20 (0.1%) | 7 111 / 7 134 |
| P010 | 2026-05-07 → 2026-05-27 | 104 128 → 104 227 (+99) | 4 427 / 4 438 | 55 344 (53.1%) | 8 217 (7.9%) | 32 794 / 38 887 |
| **Total** | | **492 518 → 494 523 (+2 005)** | **28 645 / 28 820 (99.4%)** | **246 617 (50.1%)** | **8 880 (1.8%)** | **158 465 / 165 289** |

---

## Key Findings

### 1. Triple-zero source count rows (app = 0, session = 0, network = 0)

Before injection the corpus contained **28 820** windows with zero signal counts across all three source types — representing periods the feature extractor treated as completely empty. After injection **28 645 (99.4%)** of those were eliminated.

The W30_S15 profile accounts for the vast majority of historical zeros (short lookback window of 45 s is most sensitive to gaps between heartbeats).

Remaining zeros by participant (all diagnosed as benign):
- **P001, P005, P009:** 2–3 rows each, all at end-of-recording session tails where the agent was shutting down.
- **P002:** 55 rows across 23 clusters, all at nightly scheduled shutdown boundaries (~03:25 UTC recurring). The app signal was already 0 before shutdown — legitimate idle-before-sleep, not a heartbeat miss.
- **P007, P008, P010:** 0–11 residual rows, same shutdown-tail pattern.

### 2. `has_app_data` coverage

The aggregate `has_app_data = 0` rate fell from **50.1% → 1.8%** across the corpus. Before injection, over half of all feature windows had no usable app context; after injection that drops to near-zero for 9 of 10 participants.

**P010 is the outlier at 7.9% (8 217 windows, ~33.9 h equivalent at W30_S15 slide).** Inspection confirms this is not a heartbeat injection failure: system ticks remain at full rate (38 ticks per window) while application source count is 0 — the machine was running but no foreground application was active. This represents genuine idle/desktop time on a participant who regularly left the machine unattended mid-day and overnight. The old extractor masked this with 92.5% no-app coverage; after injection the true idle proportion is visible.

### 3. `app_dwell_mean_ms = 0` with app data present

These are windows where `has_app_data = 1` (a heartbeat exists) but the aggregator found no completed dwell within the lookback range — typically a window landing entirely inside a long uninterrupted dwell. Before injection this affected **165 289** windows; after injection **158 465** were fixed (95.9%). The 6 824 residuals are structurally identical to the P010 idle-time pattern — short transitions or single-heartbeat windows without enough context to compute a mean dwell.

### 4. Extra rows added

Injection added **2 005 new rows** (0.4% growth). All new rows have `has_app_data = 1` and `app_top1_share = 1.0`, confirming they are fully-covered windows that were previously unrepresentable because no heartbeat fell in their extraction range.

---

## Per-Participant Notes

### P001 — Near-clean
51% → 0.0% `has_app_data=0`. Three residual triple-zero rows at end-of-recording (2026-05-09 ~09:00 UTC). Fully expected.

### P002 — Nightly shutdown pattern
51.1% → 2.8%. Residual 55 triple-zero rows are all session-end boundaries at ~03:25 UTC across 23 distinct nights, consistent with a scheduled shutdown or sleep policy. `has_collection_gap` is not flagging these — a gap detector miss worth investigating separately.

### P003 — Fully clean
38.6% → 0.0%. Zero residual triple-zero rows. All 1 324 historical zeros eliminated.

### N004 — Fully clean
44.4% → 0.0%. All 853 historical zeros eliminated. Smallest dataset (10 933 rows).

### P005 — Near-clean
56.8% → 0.0%. Three residual rows (one per profile) at 2026-05-19 ~04:18 UTC — single end-of-session tail. Expected.

### P006 — Fully clean
55.5% → 0.0%. Largest gain in new rows (+478), reflecting long uninterrupted dwells now covered.

### P007 — Near-clean
48.5% → 0.1%. All 4 344 historical triple-zeros eliminated. 56 residual `has_app_data=0` rows are end-of-session tails across multiple recording days.

### P008 — Near-clean
49.1% → 0.1%. All 3 073 historical triple-zeros eliminated. 39 residual rows, same pattern.

### P009 — Near-clean
45.6% → 0.1%. 2 residual triple-zero rows (W30_S15 only), end-of-session. 20 residual `has_app_data=0`.

### P010 — Outlier (legitimate idle time)
53.1% → 7.9%. Despite the apparent residual, W30_S15 `has_app_data=0` improved from **92.5% → 13.7%**, representing ~33.9 h of genuine machine-idle time (no foreground app, system running). The remaining zeros are not a heartbeat injection failure — the liveness gate correctly suppresses heartbeats when no app is in focus. Old data was masking this completely.

---

## Methodology

- **Old db:** `DataBase/{N}/features.db` — extracted before heartbeat injection
- **New db:** `DataBase/{N}/participant_PXXX/features.db` — extracted with `AppDwellReplayPreprocessor.InjectHeartbeats` enabled
- **Triple-zero:** `source_counts_json` application = 0 AND session = 0 AND network = 0
- **`has_app_data`:** read directly from `features_json`
- **`dwell_mean=0` with app:** `has_app_data = 1` AND `app_dwell_mean_ms = 0`
- Analysis covers all three window profiles (W30_S15, W60_S30, W120_S60) combined unless otherwise noted
