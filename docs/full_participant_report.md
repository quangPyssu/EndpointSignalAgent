# Full Participant Dataset Report

**Generated:** 2026-06-13T09:08:20Z  
**Participants:** 15  
**Gap threshold:** 300 s | **Drain lookback:** 180 s | **Warm-up:** 120 s  
> DB type: `new` = replayed with heartbeat injection (`participant_PXXX/features.db`); `old` = pre-injection (`features.db`).

---

## Table 1 -- Collection Overview (all profiles)

| DB | Participant | DB | Start | End | Days | Total Rows | W30_S15 Rows |
|----|-------------|-----|-------|-----|------|-----------|-------------|
| 1 | P001 | new | 2026-04-24 | 2026-05-18 | 24.1 | 44,659 | 25,394 |
| 2 | P002 | new | 2026-05-06 | 2026-05-26 | 20.1 | 18,163 | 10,214 |
| 3 | P003 | new | 2026-05-03 | 2026-05-23 | 20.1 | 72,332 | 41,249 |
| 4 | N004 | new | 2026-05-07 | 2026-06-07 | 30.6 | 10,933 | 6,190 |
| 5 | P005 | new | 2026-05-05 | 2026-05-31 | 26.0 | 82,784 | 47,277 |
| 6 | P006 | new | 2026-04-30 | 2026-05-19 | 19.3 | 37,502 | 21,178 |
| 7 | P007 | new | 2026-05-03 | 2026-05-20 | 16.9 | 61,501 | 34,885 |
| 8 | P008 | new | 2026-05-04 | 2026-05-23 | 18.7 | 36,563 | 20,812 |
| 9 | P009 | new | 2026-05-06 | 2026-05-27 | 20.9 | 25,859 | 14,635 |
| 10 | P010 | new | 2026-05-07 | 2026-05-27 | 20.1 | 104,227 | 59,479 |
| 11 | P011 | new | 2026-05-04 | 2026-05-19 | 15.0 | 38,351 | 21,833 |
| 12 | P012 | new | 2026-05-05 | 2026-05-20 | 15.2 | 21,199 | 12,010 |
| 13 | P013 | new | 2026-05-04 | 2026-05-18 | 14.2 | 110,489 | 63,130 |
| 14 | P014 | new | 2026-05-05 | 2026-05-21 | 15.9 | 14,652 | 8,348 |
| 15 | P015 | new | 2026-05-06 | 2026-05-23 | 17.1 | 39,734 | 22,636 |
| **--** | **TOTAL** | | | | | **718,948** | **409,270** |

---

## Table 2 -- Gap & Quality Analysis (all profiles combined)

| DB | Participant | DB | Gaps | Drain rows | Warm-up rows | Clean rows | Data loss % |
|----|-------------|-----|------|-----------|-------------|-----------|------------|
| 1 | P001 | new | 33 | 15 | 371 | 43,728 | 0.9% |
| 2 | P002 | new | 43 | 197 | 478 | 17,147 | 3.7% |
| 3 | P003 | new | 23 | 6 | 268 | 71,693 | 0.4% |
| 4 | N004 | new | 16 | 0 | 171 | 10,505 | 1.6% |
| 5 | P005 | new | 6 | 4 | 64 | 82,593 | 0.1% |
| 6 | P006 | new | 72 | 6 | 800 | 35,568 | 2.1% |
| 7 | P007 | new | 63 | 1 | 671 | 59,694 | 1.1% |
| 8 | P008 | new | 23 | 35 | 247 | 36,045 | 0.8% |
| 9 | P009 | new | 35 | 9 | 373 | 24,872 | 1.5% |
| 10 | P010 | new | 22 | 72 | 238 | 103,661 | 0.3% |
| 11 | P011 | new | 18 | 0 | 185 | 37,795 | 0.5% |
| 12 | P012 | new | 28 | 11 | 282 | 20,516 | 1.4% |
| 13 | P013 | new | 2 | 0 | 25 | 110,435 | 0.0% |
| 14 | P014 | new | 7 | 0 | 74 | 14,455 | 0.5% |
| 15 | P015 | new | 19 | 18 | 196 | 39,282 | 0.5% |
| **--** | **TOTAL** | | **410** | **374** | **4,443** | **707,989** | **0.7%** |

> **Drain rows:** has_system_data=0 AND has_app_data=0, within 180 s before a gap.  
> **Warm-up rows:** within 120 s after gap resume.  
> **Clean rows:** not drain, not warm-up, has_system_data=1.  
> **Data loss %** = (drain + warm-up) / total rows.

---

## Table 3 -- App Signal Coverage (W30_S15)

| DB | Participant | DB | has_app_data=1 | has_app_data=0 | App Coverage % | Active windows (top1>0) | Active % |
|----|-------------|-----|---------------|---------------|---------------|------------------------|---------|
| 1 | P001 | new | 25,382 | 12 | 100.0% | 25,369 | 99.9% |
| 2 | P002 | new | 9,882 | 332 | 96.7% | 9,861 | 96.5% |
| 3 | P003 | new | 41,246 | 3 | 100.0% | 41,235 | 100.0% |
| 4 | N004 | new | 6,190 | 0 | 100.0% | 6,177 | 99.8% |
| 5 | P005 | new | 47,268 | 9 | 100.0% | 47,258 | 100.0% |
| 6 | P006 | new | 21,175 | 3 | 100.0% | 21,099 | 99.6% |
| 7 | P007 | new | 34,837 | 48 | 99.9% | 34,760 | 99.6% |
| 8 | P008 | new | 20,788 | 24 | 99.9% | 20,754 | 99.7% |
| 9 | P009 | new | 14,622 | 13 | 99.9% | 14,611 | 99.8% |
| 10 | P010 | new | 51,346 | 8,133 | 86.3% | 51,308 | 86.3% |
| 11 | P011 | new | 21,498 | 335 | 98.5% | 21,474 | 98.4% |
| 12 | P012 | new | 11,995 | 15 | 99.9% | 11,984 | 99.8% |
| 13 | P013 | new | 63,110 | 20 | 100.0% | 63,100 | 100.0% |
| 14 | P014 | new | 8,347 | 1 | 100.0% | 8,339 | 99.9% |
| 15 | P015 | new | 22,614 | 22 | 99.9% | 22,593 | 99.8% |
| **--** | **TOTAL** | | **400,300** | **8,970** | **97.8%** | **399,922** | **97.7%** |

> **App Coverage %** = fraction of W30_S15 windows where at least one app heartbeat landed.  
> **Active windows** = windows where app_top1_share > 0 (measurable foreground app dwell).

---

## Table 4 -- System-Only Rows (W30_S15)

| DB | Participant | DB | System-only rows | System-only % |
|----|-------------|-----|-----------------|--------------|
| 1 | P001 | new | 1 | 0.0% |
| 2 | P002 | new | 37 | 0.4% |
| 3 | P003 | new | 0 | 0.0% |
| 4 | N004 | new | 0 | 0.0% |
| 5 | P005 | new | 2 | 0.0% |
| 6 | P006 | new | 0 | 0.0% |
| 7 | P007 | new | 0 | 0.0% |
| 8 | P008 | new | 0 | 0.0% |
| 9 | P009 | new | 2 | 0.0% |
| 10 | P010 | new | 6 | 0.0% |
| 11 | P011 | new | 0 | 0.0% |
| 12 | P012 | new | 0 | 0.0% |
| 13 | P013 | new | 0 | 0.0% |
| 14 | P014 | new | 0 | 0.0% |
| 15 | P015 | new | 0 | 0.0% |
| **--** | **TOTAL** | | **48** | |

> **System-only rows:** app=0, session=0, network=0, system>0 in source_counts_json.  
> These pass the has_system_data=1 quality filter but carry active_work_ratio=0,
> even during sustained single-app focus. Participants >10% are candidates for the
> has_sustained_focus flag (see docs/system_only_rows_findings.md).

---

## Summary

- **Total participants:** 15
- **Total rows (all profiles):** 718,948
- **Total W30_S15 rows:** 409,270
- **Total gaps detected:** 410
- **Overall data loss:** 0.7% (4,817 rows excluded)
- **Clean rows:** 707,989
- **Overall app coverage (W30_S15):** 97.8%
- **System-only rows (W30_S15):** 48

*Generated by `scripts/full_participant_report.py` at 2026-06-13T09:08:20Z*
