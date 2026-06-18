# Markov State Versions B, C, D — Implementation and Evaluation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement and evaluate three additional Markov state schemas—vB (FocusDepth × SwitchCadence), vC (NetworkMode × ResourceMode), vD (PresenceMode × DisplayMode)—each built from feature groups NOT used in Version A, then produce per-participant findings documents parallel to `MarkovTest/results/markov_vA_findings.md`.

**Architecture:** Each new version follows the same pattern as vA: pure-function state assignment (`states_vX.py`) → unit tests → extend the shared runner (`run.py`) with a `--version` flag → run on real data → write findings document. The existing `analysis.py` and `report.py` are reused without modification; the runner renames the version-specific column to `markov_state_vA` before passing to analysis for compatibility.

**Tech Stack:** Python 3.11+, pandas, pytest (same as vA). No new dependencies.

## Global Constraints

- Working directory: `MarkovTest/` (all relative paths are relative to this folder)
- Real data: `E:/DataBase/{N}/participant_*/features.db` (SQLite, `feature_rows` table, `features_json` column)
- All state assignment functions accept a plain `dict` row (no pandas dependency)
- All tests use synthetic dicts via the `_row(**kwargs)` helper pattern from `test_states.py`
- Run all tests before and after each code change: `pytest tests/ -v`
- Feature column names must match `features_json` keys exactly (source of truth: `scripts/_feature_sample.py`)
- `has_system_data` is a top-level feature key in `features_json` (verified in `scripts/full_participant_report.py`)
- Findings documents saved to `MarkovTest/results/`

---

## Feature map — what's new in vB/vC/vD

Version A used:
- WorkMode: `has_app_data`, `cat_*_ratio`
- EngagementMode: `locked_ratio`, `active_work_ratio`, `idle_bucket_mean_sec`, `idle_ge_300_ratio`, `has_idle_data`

New features used in vB/vC/vD:

| Feature | Source group | vB | vC | vD |
|---|---|---|---|---|
| `app_top1_share` | App | ✓ | | |
| `app_switches_per_active_min` | App | ✓ | | |
| `vpn_on_ratio` | Network | | ✓ | |
| `primary_wifi_connected_ratio` | Network | | ✓ | |
| `vpn_flip_count` | Network | | ✓ | |
| `ssid_change_count` | Network | | ✓ | |
| `system_load_index` | System | | ✓ | |
| `cpu_usage_mean` | System | | ✓ | |
| `resource_variability_index` | System | | ✓ | |
| `has_system_data` | System | | ✓ | |
| `presence_present_ratio` | Session | | | ✓ |
| `presence_away_ratio` | Session | | | ✓ |
| `display_on_ratio` | Session | | | ✓ |
| `display_off_ratio` | Session | | | ✓ |

Note: `locked_ratio` is reused in vD for the `Locked` short-circuit (same rule as vA).

---

## File structure

```
MarkovTest/
  states_vB.py            — assign_focus_depth, assign_switch_cadence, assign_markov_state_vB
  states_vC.py            — assign_network_mode, assign_resource_mode, assign_markov_state_vC
  states_vD.py            — assign_presence_mode, assign_display_mode, assign_markov_state_vD
  run.py                  — (MODIFIED) add --version {vA,vB,vC,vD} flag
  tests/
    test_states_vB.py     — unit tests for all vB functions
    test_states_vC.py     — unit tests for all vC functions
    test_states_vD.py     — unit tests for all vD functions
  results/
    report_vB_W60_S30.csv
    report_vB_W120_S60.csv
    report_vC_W60_S30.csv
    report_vC_W120_S60.csv
    report_vD_W60_S30.csv
    report_vD_W120_S60.csv
    markov_vB_findings.md
    markov_vC_findings.md
    markov_vD_findings.md
```

---

## Task 1: Version B — FocusDepth × SwitchCadence

**Files:**
- Create: `states_vB.py`
- Create: `tests/test_states_vB.py`

**Interfaces:**
- Produces: `assign_focus_depth(row: dict) -> str`, `assign_switch_cadence(row: dict) -> str`, `assign_markov_state_vB(row: dict) -> str`
- Consumed by: Task 4 (`run.py`)

### Version B state definitions

**FocusDepth** — how concentrated the user's attention is on a single application (from `app_top1_share`):

| Priority | State | Rule |
|---|---|---|
| 1 | `NoApp` | `has_app_data == 0` |
| 2 | `DeepFocus` | `app_top1_share >= 0.75` |
| 3 | `SplitFocus` | `app_top1_share >= 0.45` |
| 4 | `ScatteredFocus` | `app_top1_share < 0.45` (has_app_data=1, no dominant app) |

**SwitchCadence** — how often the user switches between applications (from `app_switches_per_active_min`):

| Priority | State | Rule |
|---|---|---|
| 1 | `NoAppSwitch` | `has_app_data == 0` |
| 2 | `Rapid` | `app_switches_per_active_min >= 3.0` |
| 3 | `Moderate` | `app_switches_per_active_min >= 1.0` |
| 4 | `Static` | `app_switches_per_active_min < 1.0` |

**Composite collapsing:**
- If `FocusDepth == NoApp` → state = `"NoApp"` (SwitchCadence is meaningless without app data)
- Otherwise → `"{FocusDepth}_{SwitchCadence}"`

**Theoretical state set (max 10):**
```
NoApp
DeepFocus_Static | DeepFocus_Moderate | DeepFocus_Rapid
SplitFocus_Static | SplitFocus_Moderate | SplitFocus_Rapid
ScatteredFocus_Static | ScatteredFocus_Moderate | ScatteredFocus_Rapid
```

- [ ] **Step 1: Write failing tests**

Create `tests/test_states_vB.py`:

```python
import pytest
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vB import assign_focus_depth, assign_switch_cadence, assign_markov_state_vB


def _row(**kwargs):
    defaults = dict(
        has_app_data=1,
        app_top1_share=0.0,
        app_switch_count=0,
        app_switches_per_active_min=0.0,
    )
    defaults.update(kwargs)
    return defaults


# ── assign_focus_depth ────────────────────────────────────────────────────────

def test_focus_no_app():
    assert assign_focus_depth(_row(has_app_data=0)) == "NoApp"

def test_focus_deep():
    assert assign_focus_depth(_row(app_top1_share=0.80)) == "DeepFocus"

def test_focus_deep_exact_threshold():
    assert assign_focus_depth(_row(app_top1_share=0.75)) == "DeepFocus"

def test_focus_deep_below_threshold():
    assert assign_focus_depth(_row(app_top1_share=0.74)) == "SplitFocus"

def test_focus_split():
    assert assign_focus_depth(_row(app_top1_share=0.55)) == "SplitFocus"

def test_focus_split_exact_threshold():
    assert assign_focus_depth(_row(app_top1_share=0.45)) == "SplitFocus"

def test_focus_split_below_threshold():
    assert assign_focus_depth(_row(app_top1_share=0.44)) == "ScatteredFocus"

def test_focus_scattered():
    assert assign_focus_depth(_row(app_top1_share=0.20)) == "ScatteredFocus"

def test_focus_scattered_zero():
    assert assign_focus_depth(_row(app_top1_share=0.0)) == "ScatteredFocus"


# ── assign_switch_cadence ─────────────────────────────────────────────────────

def test_cadence_no_app():
    assert assign_switch_cadence(_row(has_app_data=0)) == "NoAppSwitch"

def test_cadence_rapid():
    assert assign_switch_cadence(_row(app_switches_per_active_min=3.5)) == "Rapid"

def test_cadence_rapid_exact_threshold():
    assert assign_switch_cadence(_row(app_switches_per_active_min=3.0)) == "Rapid"

def test_cadence_rapid_below():
    assert assign_switch_cadence(_row(app_switches_per_active_min=2.9)) == "Moderate"

def test_cadence_moderate():
    assert assign_switch_cadence(_row(app_switches_per_active_min=1.5)) == "Moderate"

def test_cadence_moderate_exact_threshold():
    assert assign_switch_cadence(_row(app_switches_per_active_min=1.0)) == "Moderate"

def test_cadence_moderate_below():
    assert assign_switch_cadence(_row(app_switches_per_active_min=0.9)) == "Static"

def test_cadence_static():
    assert assign_switch_cadence(_row(app_switches_per_active_min=0.3)) == "Static"

def test_cadence_static_zero():
    assert assign_switch_cadence(_row(app_switches_per_active_min=0.0)) == "Static"


# ── assign_markov_state_vB ────────────────────────────────────────────────────

def test_vB_no_app_collapses():
    r = _row(has_app_data=0)
    assert assign_markov_state_vB(r) == "NoApp"

def test_vB_deep_focus_static():
    r = _row(app_top1_share=0.85, app_switches_per_active_min=0.5)
    assert assign_markov_state_vB(r) == "DeepFocus_Static"

def test_vB_deep_focus_rapid():
    r = _row(app_top1_share=0.80, app_switches_per_active_min=4.0)
    assert assign_markov_state_vB(r) == "DeepFocus_Rapid"

def test_vB_split_moderate():
    r = _row(app_top1_share=0.55, app_switches_per_active_min=1.5)
    assert assign_markov_state_vB(r) == "SplitFocus_Moderate"

def test_vB_scattered_static():
    r = _row(app_top1_share=0.20, app_switches_per_active_min=0.2)
    assert assign_markov_state_vB(r) == "ScatteredFocus_Static"

def test_vB_scattered_rapid():
    r = _row(app_top1_share=0.10, app_switches_per_active_min=5.0)
    assert assign_markov_state_vB(r) == "ScatteredFocus_Rapid"
```

- [ ] **Step 2: Run tests — verify they fail**

```
cd MarkovTest && pytest tests/test_states_vB.py -v
```

Expected: `ImportError: No module named 'states_vB'`

- [ ] **Step 3: Create states_vB.py**

```python
def assign_focus_depth(row: dict) -> str:
    if row.get("has_app_data", 0) == 0:
        return "NoApp"
    top1 = row.get("app_top1_share", 0.0)
    if top1 >= 0.75:
        return "DeepFocus"
    if top1 >= 0.45:
        return "SplitFocus"
    return "ScatteredFocus"


def assign_switch_cadence(row: dict) -> str:
    if row.get("has_app_data", 0) == 0:
        return "NoAppSwitch"
    spm = row.get("app_switches_per_active_min", 0.0)
    if spm >= 3.0:
        return "Rapid"
    if spm >= 1.0:
        return "Moderate"
    return "Static"


def assign_markov_state_vB(row: dict) -> str:
    focus = assign_focus_depth(row)
    cadence = assign_switch_cadence(row)
    if focus == "NoApp":
        return "NoApp"
    return f"{focus}_{cadence}"
```

- [ ] **Step 4: Run tests — verify all pass**

```
cd MarkovTest && pytest tests/test_states_vB.py -v
```

Expected: all 24 tests PASS.

- [ ] **Step 5: Run full test suite — verify no regressions**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS (including all existing vA tests).

- [ ] **Step 6: Commit**

```bash
git add MarkovTest/states_vB.py MarkovTest/tests/test_states_vB.py
git commit -m "feat(markov): add Version B state assignment (FocusDepth x SwitchCadence)"
```

---

## Task 2: Version C — NetworkMode × ResourceMode

**Files:**
- Create: `states_vC.py`
- Create: `tests/test_states_vC.py`

**Interfaces:**
- Produces: `assign_network_mode(row: dict) -> str`, `assign_resource_mode(row: dict) -> str`, `assign_markov_state_vC(row: dict) -> str`
- Consumed by: Task 4 (`run.py`)

### Version C state definitions

**NetworkMode** — connectivity context and stability (from `vpn_on_ratio`, `primary_wifi_connected_ratio`, `vpn_flip_count`, `ssid_change_count`):

| Priority | State | Rule |
|---|---|---|
| 1 | `Offline` | `primary_wifi_connected_ratio < 0.25 and vpn_on_ratio < 0.10` |
| 2 | `VPNActive` | `vpn_on_ratio >= 0.50` |
| 3 | `NetworkFlux` | `vpn_flip_count > 1 or ssid_change_count > 0` |
| 4 | `OnlineSecure` | `primary_wifi_connected_ratio >= 0.75` |
| 5 | `OnlineMixed` | `primary_wifi_connected_ratio >= 0.25` |
| 6 | `UnknownNetwork` | none of the above |

**ResourceMode** — CPU/system load profile (from `system_load_index`, `resource_variability_index`, `cpu_usage_mean`, `has_system_data`):

| Priority | State | Rule |
|---|---|---|
| 1 | `NoSystemData` | `has_system_data == 0` |
| 2 | `HeavyLoad` | `system_load_index >= 0.65` |
| 3 | `VariableLoad` | `resource_variability_index >= 0.35` |
| 4 | `ModerateLoad` | `cpu_usage_mean >= 0.30` |
| 5 | `LightLoad` | otherwise |

**Composite:** `"{NetworkMode}_{ResourceMode}"` (no collapsing — all combinations carry meaning)

**Theoretical state set (max 30):** 6 NetworkMode × 5 ResourceMode. In practice expect 10–18 per participant.

- [ ] **Step 1: Write failing tests**

Create `tests/test_states_vC.py`:

```python
import pytest
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vC import assign_network_mode, assign_resource_mode, assign_markov_state_vC


def _row(**kwargs):
    defaults = dict(
        has_system_data=1,
        primary_wifi_connected_ratio=0.80,
        vpn_on_ratio=0.0,
        vpn_flip_count=0,
        ssid_change_count=0,
        system_load_index=0.20,
        cpu_usage_mean=0.15,
        resource_variability_index=0.10,
    )
    defaults.update(kwargs)
    return defaults


# ── assign_network_mode ───────────────────────────────────────────────────────

def test_network_offline():
    r = _row(primary_wifi_connected_ratio=0.10, vpn_on_ratio=0.05)
    assert assign_network_mode(r) == "Offline"

def test_network_offline_exact_thresholds():
    # both at boundary: wifi=0.24 < 0.25, vpn=0.09 < 0.10 → Offline
    r = _row(primary_wifi_connected_ratio=0.24, vpn_on_ratio=0.09)
    assert assign_network_mode(r) == "Offline"

def test_network_vpn_active():
    r = _row(vpn_on_ratio=0.70, primary_wifi_connected_ratio=0.80)
    assert assign_network_mode(r) == "VPNActive"

def test_network_vpn_active_exact_threshold():
    r = _row(vpn_on_ratio=0.50)
    assert assign_network_mode(r) == "VPNActive"

def test_network_vpn_below_threshold_falls_to_online_secure():
    # vpn=0.40 < 0.50, no flux, wifi=0.80 >= 0.75 → OnlineSecure
    r = _row(vpn_on_ratio=0.40, primary_wifi_connected_ratio=0.80, vpn_flip_count=0, ssid_change_count=0)
    assert assign_network_mode(r) == "OnlineSecure"

def test_network_flux_by_vpn_flips():
    r = _row(vpn_flip_count=2, primary_wifi_connected_ratio=0.80, vpn_on_ratio=0.10)
    assert assign_network_mode(r) == "NetworkFlux"

def test_network_flux_by_ssid_change():
    r = _row(ssid_change_count=1, vpn_flip_count=0, primary_wifi_connected_ratio=0.80, vpn_on_ratio=0.10)
    assert assign_network_mode(r) == "NetworkFlux"

def test_network_flux_priority_over_online_secure():
    # wifi=0.80 would normally be OnlineSecure, but ssid change bumps to NetworkFlux
    r = _row(ssid_change_count=1, primary_wifi_connected_ratio=0.80, vpn_on_ratio=0.0, vpn_flip_count=0)
    assert assign_network_mode(r) == "NetworkFlux"

def test_network_online_secure():
    r = _row(primary_wifi_connected_ratio=0.90, vpn_on_ratio=0.0, vpn_flip_count=0, ssid_change_count=0)
    assert assign_network_mode(r) == "OnlineSecure"

def test_network_online_secure_exact_threshold():
    r = _row(primary_wifi_connected_ratio=0.75, vpn_on_ratio=0.0, vpn_flip_count=0, ssid_change_count=0)
    assert assign_network_mode(r) == "OnlineSecure"

def test_network_online_mixed():
    r = _row(primary_wifi_connected_ratio=0.50, vpn_on_ratio=0.0, vpn_flip_count=0, ssid_change_count=0)
    assert assign_network_mode(r) == "OnlineMixed"

def test_network_online_mixed_exact_threshold():
    r = _row(primary_wifi_connected_ratio=0.25, vpn_on_ratio=0.0, vpn_flip_count=0, ssid_change_count=0)
    assert assign_network_mode(r) == "OnlineMixed"

def test_network_unknown():
    # wifi=0.20 (not offline because vpn=0.15 >= 0.10); vpn=0.15 < 0.50; no flux; wifi < 0.25 → UnknownNetwork
    r = _row(primary_wifi_connected_ratio=0.20, vpn_on_ratio=0.15, vpn_flip_count=0, ssid_change_count=0)
    assert assign_network_mode(r) == "UnknownNetwork"


# ── assign_resource_mode ──────────────────────────────────────────────────────

def test_resource_no_system_data():
    r = _row(has_system_data=0)
    assert assign_resource_mode(r) == "NoSystemData"

def test_resource_heavy_load():
    r = _row(system_load_index=0.70)
    assert assign_resource_mode(r) == "HeavyLoad"

def test_resource_heavy_load_exact_threshold():
    r = _row(system_load_index=0.65)
    assert assign_resource_mode(r) == "HeavyLoad"

def test_resource_heavy_load_below():
    r = _row(system_load_index=0.64, resource_variability_index=0.10, cpu_usage_mean=0.20)
    assert assign_resource_mode(r) == "LightLoad"

def test_resource_variable():
    r = _row(system_load_index=0.20, resource_variability_index=0.40)
    assert assign_resource_mode(r) == "VariableLoad"

def test_resource_variable_exact_threshold():
    r = _row(system_load_index=0.20, resource_variability_index=0.35)
    assert assign_resource_mode(r) == "VariableLoad"

def test_resource_moderate():
    r = _row(system_load_index=0.20, resource_variability_index=0.10, cpu_usage_mean=0.40)
    assert assign_resource_mode(r) == "ModerateLoad"

def test_resource_moderate_exact_threshold():
    r = _row(system_load_index=0.20, resource_variability_index=0.10, cpu_usage_mean=0.30)
    assert assign_resource_mode(r) == "ModerateLoad"

def test_resource_light():
    r = _row(system_load_index=0.10, resource_variability_index=0.10, cpu_usage_mean=0.15)
    assert assign_resource_mode(r) == "LightLoad"

def test_resource_light_zero():
    r = _row(system_load_index=0.0, resource_variability_index=0.0, cpu_usage_mean=0.0)
    assert assign_resource_mode(r) == "LightLoad"


# ── assign_markov_state_vC ────────────────────────────────────────────────────

def test_vC_vpn_heavy():
    r = _row(vpn_on_ratio=0.70, system_load_index=0.70)
    assert assign_markov_state_vC(r) == "VPNActive_HeavyLoad"

def test_vC_online_secure_light():
    r = _row(primary_wifi_connected_ratio=0.90, vpn_on_ratio=0.0,
             system_load_index=0.10, resource_variability_index=0.05, cpu_usage_mean=0.10)
    assert assign_markov_state_vC(r) == "OnlineSecure_LightLoad"

def test_vC_offline_no_system_data():
    r = _row(primary_wifi_connected_ratio=0.10, vpn_on_ratio=0.05, has_system_data=0)
    assert assign_markov_state_vC(r) == "Offline_NoSystemData"

def test_vC_flux_variable():
    r = _row(ssid_change_count=1, primary_wifi_connected_ratio=0.80,
             vpn_on_ratio=0.0, system_load_index=0.20, resource_variability_index=0.40)
    assert assign_markov_state_vC(r) == "NetworkFlux_VariableLoad"
```

- [ ] **Step 2: Run tests — verify they fail**

```
cd MarkovTest && pytest tests/test_states_vC.py -v
```

Expected: `ImportError: No module named 'states_vC'`

- [ ] **Step 3: Create states_vC.py**

```python
def assign_network_mode(row: dict) -> str:
    wifi = row.get("primary_wifi_connected_ratio", 0.0)
    vpn = row.get("vpn_on_ratio", 0.0)
    vpn_flips = row.get("vpn_flip_count", 0)
    ssid_changes = row.get("ssid_change_count", 0)

    if wifi < 0.25 and vpn < 0.10:
        return "Offline"
    if vpn >= 0.50:
        return "VPNActive"
    if vpn_flips > 1 or ssid_changes > 0:
        return "NetworkFlux"
    if wifi >= 0.75:
        return "OnlineSecure"
    if wifi >= 0.25:
        return "OnlineMixed"
    return "UnknownNetwork"


def assign_resource_mode(row: dict) -> str:
    if row.get("has_system_data", 1) == 0:
        return "NoSystemData"
    load = row.get("system_load_index", 0.0)
    variability = row.get("resource_variability_index", 0.0)
    cpu = row.get("cpu_usage_mean", 0.0)

    if load >= 0.65:
        return "HeavyLoad"
    if variability >= 0.35:
        return "VariableLoad"
    if cpu >= 0.30:
        return "ModerateLoad"
    return "LightLoad"


def assign_markov_state_vC(row: dict) -> str:
    network = assign_network_mode(row)
    resource = assign_resource_mode(row)
    return f"{network}_{resource}"
```

- [ ] **Step 4: Run tests — verify all pass**

```
cd MarkovTest && pytest tests/test_states_vC.py -v
```

Expected: all 27 tests PASS.

- [ ] **Step 5: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add MarkovTest/states_vC.py MarkovTest/tests/test_states_vC.py
git commit -m "feat(markov): add Version C state assignment (NetworkMode x ResourceMode)"
```

---

## Task 3: Version D — PresenceMode × DisplayMode

**Files:**
- Create: `states_vD.py`
- Create: `tests/test_states_vD.py`

**Interfaces:**
- Produces: `assign_presence_mode(row: dict) -> str`, `assign_display_mode(row: dict) -> str`, `assign_markov_state_vD(row: dict) -> str`
- Consumed by: Task 4 (`run.py`)

### Version D state definitions

**PresenceMode** — physical user presence and lock status (from `locked_ratio`, `presence_present_ratio`, `presence_away_ratio`):

| Priority | State | Rule |
|---|---|---|
| 1 | `Locked` | `locked_ratio >= 0.80` |
| 2 | `ConfirmedPresent` | `presence_present_ratio >= 0.70` |
| 3 | `ProbablyAway` | `presence_away_ratio >= 0.70` |
| 4 | `AmbiguousPresence` | `presence_present_ratio > 0 or presence_away_ratio > 0` (weak signal) |
| 5 | `UnknownPresence` | both ratios are 0 (no presence sensor data) |

**DisplayMode** — screen on/off patterns (from `display_on_ratio`, `display_off_ratio`):

| Priority | State | Rule |
|---|---|---|
| 1 | `DisplayAlwaysOn` | `display_on_ratio >= 0.85` |
| 2 | `DisplayMostlyOn` | `display_on_ratio >= 0.50` |
| 3 | `DisplayOff` | `display_off_ratio >= 0.60` |
| 4 | `DisplayMixed` | none of the above |

**Composite collapsing:**
- If `PresenceMode == Locked` → state = `"Locked"` (display state during lock is not meaningful)
- Otherwise → `"{PresenceMode}_{DisplayMode}"`

**Theoretical state set (max 17):**
```
Locked
ConfirmedPresent_DisplayAlwaysOn | ConfirmedPresent_DisplayMostlyOn | ConfirmedPresent_DisplayOff | ConfirmedPresent_DisplayMixed
ProbablyAway_...  (same 4 DisplayMode suffixes)
AmbiguousPresence_...
UnknownPresence_...
```

- [ ] **Step 1: Write failing tests**

Create `tests/test_states_vD.py`:

```python
import pytest
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vD import assign_presence_mode, assign_display_mode, assign_markov_state_vD


def _row(**kwargs):
    defaults = dict(
        locked_ratio=0.0,
        presence_present_ratio=0.0,
        presence_away_ratio=0.0,
        display_on_ratio=0.90,
        display_off_ratio=0.05,
    )
    defaults.update(kwargs)
    return defaults


# ── assign_presence_mode ──────────────────────────────────────────────────────

def test_presence_locked():
    r = _row(locked_ratio=0.85)
    assert assign_presence_mode(r) == "Locked"

def test_presence_locked_exact_threshold():
    r = _row(locked_ratio=0.80)
    assert assign_presence_mode(r) == "Locked"

def test_presence_locked_below_falls_to_confirmed_present():
    r = _row(locked_ratio=0.79, presence_present_ratio=0.80)
    assert assign_presence_mode(r) == "ConfirmedPresent"

def test_presence_confirmed_present():
    r = _row(presence_present_ratio=0.80)
    assert assign_presence_mode(r) == "ConfirmedPresent"

def test_presence_confirmed_present_exact_threshold():
    r = _row(presence_present_ratio=0.70)
    assert assign_presence_mode(r) == "ConfirmedPresent"

def test_presence_confirmed_present_below_ambiguous():
    r = _row(presence_present_ratio=0.69, presence_away_ratio=0.20)
    assert assign_presence_mode(r) == "AmbiguousPresence"

def test_presence_probably_away():
    r = _row(presence_away_ratio=0.80)
    assert assign_presence_mode(r) == "ProbablyAway"

def test_presence_probably_away_exact_threshold():
    r = _row(presence_away_ratio=0.70)
    assert assign_presence_mode(r) == "ProbablyAway"

def test_presence_probably_away_below_ambiguous():
    r = _row(presence_away_ratio=0.69, presence_present_ratio=0.20)
    assert assign_presence_mode(r) == "AmbiguousPresence"

def test_presence_ambiguous_both_weak():
    r = _row(presence_present_ratio=0.40, presence_away_ratio=0.40)
    assert assign_presence_mode(r) == "AmbiguousPresence"

def test_presence_unknown_both_zero():
    r = _row(presence_present_ratio=0.0, presence_away_ratio=0.0)
    assert assign_presence_mode(r) == "UnknownPresence"


# ── assign_display_mode ───────────────────────────────────────────────────────

def test_display_always_on():
    r = _row(display_on_ratio=0.90)
    assert assign_display_mode(r) == "DisplayAlwaysOn"

def test_display_always_on_exact_threshold():
    r = _row(display_on_ratio=0.85)
    assert assign_display_mode(r) == "DisplayAlwaysOn"

def test_display_always_on_below_mostly_on():
    r = _row(display_on_ratio=0.84, display_off_ratio=0.10)
    assert assign_display_mode(r) == "DisplayMostlyOn"

def test_display_mostly_on():
    r = _row(display_on_ratio=0.65, display_off_ratio=0.30)
    assert assign_display_mode(r) == "DisplayMostlyOn"

def test_display_mostly_on_exact_threshold():
    r = _row(display_on_ratio=0.50)
    assert assign_display_mode(r) == "DisplayMostlyOn"

def test_display_mostly_on_below_display_off():
    r = _row(display_on_ratio=0.30, display_off_ratio=0.65)
    assert assign_display_mode(r) == "DisplayOff"

def test_display_off():
    r = _row(display_on_ratio=0.10, display_off_ratio=0.80)
    assert assign_display_mode(r) == "DisplayOff"

def test_display_off_exact_threshold():
    r = _row(display_on_ratio=0.30, display_off_ratio=0.60)
    assert assign_display_mode(r) == "DisplayOff"

def test_display_mixed():
    r = _row(display_on_ratio=0.40, display_off_ratio=0.40)
    assert assign_display_mode(r) == "DisplayMixed"


# ── assign_markov_state_vD ────────────────────────────────────────────────────

def test_vD_locked_collapses():
    r = _row(locked_ratio=0.90, display_on_ratio=0.0, display_off_ratio=1.0)
    assert assign_markov_state_vD(r) == "Locked"

def test_vD_confirmed_present_always_on():
    r = _row(presence_present_ratio=0.80, display_on_ratio=0.90)
    assert assign_markov_state_vD(r) == "ConfirmedPresent_DisplayAlwaysOn"

def test_vD_probably_away_display_off():
    r = _row(presence_away_ratio=0.80, display_on_ratio=0.10, display_off_ratio=0.80)
    assert assign_markov_state_vD(r) == "ProbablyAway_DisplayOff"

def test_vD_unknown_presence_mostly_on():
    r = _row(presence_present_ratio=0.0, presence_away_ratio=0.0,
             display_on_ratio=0.65, display_off_ratio=0.20)
    assert assign_markov_state_vD(r) == "UnknownPresence_DisplayMostlyOn"

def test_vD_ambiguous_mixed():
    r = _row(presence_present_ratio=0.40, presence_away_ratio=0.35,
             display_on_ratio=0.40, display_off_ratio=0.40)
    assert assign_markov_state_vD(r) == "AmbiguousPresence_DisplayMixed"
```

- [ ] **Step 2: Run tests — verify they fail**

```
cd MarkovTest && pytest tests/test_states_vD.py -v
```

Expected: `ImportError: No module named 'states_vD'`

- [ ] **Step 3: Create states_vD.py**

```python
def assign_presence_mode(row: dict) -> str:
    if row.get("locked_ratio", 0.0) >= 0.80:
        return "Locked"
    present = row.get("presence_present_ratio", 0.0)
    away = row.get("presence_away_ratio", 0.0)
    if present >= 0.70:
        return "ConfirmedPresent"
    if away >= 0.70:
        return "ProbablyAway"
    if present > 0 or away > 0:
        return "AmbiguousPresence"
    return "UnknownPresence"


def assign_display_mode(row: dict) -> str:
    on = row.get("display_on_ratio", 0.0)
    off = row.get("display_off_ratio", 0.0)
    if on >= 0.85:
        return "DisplayAlwaysOn"
    if on >= 0.50:
        return "DisplayMostlyOn"
    if off >= 0.60:
        return "DisplayOff"
    return "DisplayMixed"


def assign_markov_state_vD(row: dict) -> str:
    presence = assign_presence_mode(row)
    display = assign_display_mode(row)
    if presence == "Locked":
        return "Locked"
    return f"{presence}_{display}"
```

- [ ] **Step 4: Run tests — verify all pass**

```
cd MarkovTest && pytest tests/test_states_vD.py -v
```

Expected: all 25 tests PASS.

- [ ] **Step 5: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add MarkovTest/states_vD.py MarkovTest/tests/test_states_vD.py
git commit -m "feat(markov): add Version D state assignment (PresenceMode x DisplayMode)"
```

---

## Task 4: Extend run.py with multi-version support

**Files:**
- Modify: `run.py`

**Interfaces:**
- Consumes: `assign_markov_state_vB` from `states_vB`, `assign_markov_state_vC` from `states_vC`, `assign_markov_state_vD` from `states_vD`
- The existing `analysis.py` / `report.py` are NOT modified — they always receive a column named `markov_state_vA`; the runner renames the version column before passing to analysis

- [ ] **Step 1: Replace run.py**

```python
"""
Usage:
    python run.py                                      # vA, W60_S30, all 15 participants
    python run.py --version vB --profile W60_S30
    python run.py --version vC --out results/report_vC_W60_S30.csv
    python run.py --version vD --participants 1 2 3
"""
import argparse
import sys
from pathlib import Path

from loader import load_all_participants, DEFAULT_PROFILES
from states import assign_markov_state_vA
from states_vB import assign_markov_state_vB
from states_vC import assign_markov_state_vC
from states_vD import assign_markov_state_vD
from report import build_participant_report, print_summary, save_reports_csv

_VERSION_FN = {
    "vA": assign_markov_state_vA,
    "vB": assign_markov_state_vB,
    "vC": assign_markov_state_vC,
    "vD": assign_markov_state_vD,
}


def main():
    parser = argparse.ArgumentParser(description="Markov State evaluation — multi-version")
    parser.add_argument("--version", choices=list(_VERSION_FN), default="vA",
                        help="State schema version (default: vA)")
    parser.add_argument("--profile", choices=DEFAULT_PROFILES, default="W60_S30")
    parser.add_argument(
        "--participants", nargs="*", type=int, default=None,
        help="Folder numbers to include (e.g. 1 2 3). Default: all 1-15.",
    )
    parser.add_argument(
        "--out", default=None,
        help="Output CSV path. Default: results/report_{VERSION}_{PROFILE}.csv",
    )
    args = parser.parse_args()

    out_path = args.out or f"results/report_{args.version}_{args.profile}.csv"
    participant_numbers = args.participants or list(range(1, 16))

    print(f"Loading version={args.version} profile={args.profile} "
          f"for {len(participant_numbers)} participant(s)...")
    df = load_all_participants(
        participant_folder_numbers=participant_numbers,
        profiles=[args.profile],
    )

    if df.empty:
        print("ERROR: No data loaded. Check E:/DataBase paths.", file=sys.stderr)
        sys.exit(1)

    state_col = f"markov_state_{args.version}"
    assign_fn = _VERSION_FN[args.version]

    print(f"Loaded {len(df):,} rows ({df['is_abnormal'].sum():,} abnormal tagged). "
          f"Assigning {state_col}...")
    df[state_col] = df.apply(assign_fn, axis=1)

    print(f"\nTop states overall:\n{df[state_col].value_counts().head(10).to_string()}\n")

    # analysis.py expects "markov_state_vA" — rename for compatibility
    analysis_df = df.rename(columns={state_col: "markov_state_vA"})

    participant_ids = sorted(analysis_df["participant_id"].unique())
    reports = [build_participant_report(analysis_df, pid) for pid in participant_ids]

    print(f"\n=== Per-participant summary ({args.version} / {args.profile}) ===")
    print_summary(reports)

    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    save_reports_csv(reports, out_path)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Confirm vA smoke test still works**

```
cd MarkovTest && python run.py --version vA --profile W60_S30 --participants 1
```

Expected: same behavior as before — participant 001 summary printed, CSV saved to `results/report_vA_W60_S30.csv`.

- [ ] **Step 3: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/run.py
git commit -m "feat(markov): extend run.py with --version flag for vA/vB/vC/vD"
```

---

## Task 5: Run Version B and write findings

**Files:**
- Create: `results/markov_vB_findings.md`

- [ ] **Step 1: Run vB on all participants, W60_S30**

```
cd MarkovTest && python run.py --version vB --profile W60_S30
```

Note the console output: total windows, state frequency table, and the per-participant summary table (PID / Windows / States / Transitions / RarePct / NormalUnseen / AbnUnseen).

- [ ] **Step 2: Run vB on W120_S60 for comparison**

```
cd MarkovTest && python run.py --version vB --profile W120_S60
```

- [ ] **Step 3: Create results/markov_vB_findings.md**

Fill all `[FILL]` placeholders from the Step 1 console output. Use the template:

```markdown
# Markov State Version B — Evaluation Findings

**Profile evaluated:** W60_S30 (60-second windows, 30-second slide)
**Participants:** 15
**Total windows:** [FILL — from console "Loaded N rows"]
**State schema:** FocusDepth × SwitchCadence

---

## What Version B is

Version B maps each feature window to a single Markov state:

```
markov_state_vB = FocusDepth + SwitchCadence
```

**FocusDepth** captures how concentrated the user's attention is on a single application:

| Priority | State | Rule |
|---|---|---|
| 1 | `NoApp` | `has_app_data == 0` |
| 2 | `DeepFocus` | `app_top1_share >= 0.75` |
| 3 | `SplitFocus` | `app_top1_share >= 0.45` |
| 4 | `ScatteredFocus` | `app_top1_share < 0.45` |

**SwitchCadence** captures how often the user switches between applications:

| Priority | State | Rule |
|---|---|---|
| 1 | `NoAppSwitch` | `has_app_data == 0` |
| 2 | `Rapid` | `app_switches_per_active_min >= 3.0` |
| 3 | `Moderate` | `app_switches_per_active_min >= 1.0` |
| 4 | `Static` | `app_switches_per_active_min < 1.0` |

**Collapsing rule:** If `FocusDepth == NoApp` → state is just `NoApp`.

**Theoretical maximum: 10 states** (1 + 3×3). In practice expect 6–9 per participant.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| [FILL from console top-10 state counts] | |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| [FILL each row from console summary table] | | | | | | |

**— = no annotation data for this participant (is_abnormal not set)**

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant ≤ 10 (theoretical max)

**Result:** [FILL — range X–Y]

**Verdict:** PASS/MARGINAL/FAIL. [One sentence explaining why.]

### Criterion 2: RarePct < 60% for most participants

**Result:** [FILL — range X%–Y%]

**Verdict:** PASS/MARGINAL/FAIL. [One sentence explaining why. vB has a much smaller state space than vA so RarePct should be lower.]

### Criterion 3: NormalUnseen < 20% for all participants (critical)

**Result:** [FILL — range X%–Y%]

**Verdict:** PASS/MARGINAL/FAIL. [This is the critical feasibility criterion.]

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal |
|---|---|---|---|
| 001 | [FILL] | [FILL] | [Slight elevation / Strong elevation / No signal] |
| 005 | [FILL] | [FILL] | |
| 006 | [FILL] | [FILL] | |
| 011 | [FILL] | [FILL] | |

**Verdict:** [FILL]

---

## Comparison to Version A

| Metric | Version A | Version B |
|---|---|---|
| Theoretical max states | 53 | 10 |
| Observed state range | 18–35 | [FILL] |
| RarePct range | 52.9%–69.9% | [FILL] |
| NormalUnseen range | 0.0%–5.6% | [FILL] |
| P005 AbnUnseen | 8.3% | [FILL] |
| P011 AbnUnseen | 12.5% | [FILL] |

---

## Overall verdict

[FILL — is vB feasible? Does a compact app-behavior state space carry anomaly signal? Does it complement vA (captures different behavioral deviations)?]

**Recommended next steps:**

1. [FILL based on results]
2. [FILL based on results]

---

*Generated by `MarkovTest/run.py` · Version B · Profile W60_S30 · 2026-06-18*
```

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/results/report_vB_W60_S30.csv MarkovTest/results/report_vB_W120_S60.csv MarkovTest/results/markov_vB_findings.md
git commit -m "data(markov): run Version B evaluation; add findings document"
```

---

## Task 6: Run Version C and write findings

**Files:**
- Create: `results/markov_vC_findings.md`

- [ ] **Step 1: Run vC on all participants, W60_S30**

```
cd MarkovTest && python run.py --version vC --profile W60_S30
```

Note the console output as in Task 5 Step 1.

- [ ] **Step 2: Run vC on W120_S60**

```
cd MarkovTest && python run.py --version vC --profile W120_S60
```

- [ ] **Step 3: Create results/markov_vC_findings.md**

Fill all `[FILL]` placeholders from Step 1 console output:

```markdown
# Markov State Version C — Evaluation Findings

**Profile evaluated:** W60_S30
**Participants:** 15
**Total windows:** [FILL]
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
| [FILL] | |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| [FILL each row] | | | | | | |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant ≤ 20 (well below 30 theoretical max)

**Result:** [FILL — range X–Y]

**Verdict:** PASS/MARGINAL/FAIL.

### Criterion 2: RarePct < 60%

**Result:** [FILL]

**Verdict:** PASS/MARGINAL/FAIL. [Note: vC has more states than vB so sparsity is expected to be higher.]

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** [FILL]

**Verdict:** PASS/MARGINAL/FAIL.

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal |
|---|---|---|---|
| 001 | [FILL] | [FILL] | |
| 005 | [FILL] | [FILL] | |
| 006 | [FILL] | [FILL] | |
| 011 | [FILL] | [FILL] | |

**Verdict:** [FILL — does network/resource context reveal anomalous behavior that vA misses?]

---

## Comparison to Version A

| Metric | Version A | Version C |
|---|---|---|
| Theoretical max states | 53 | 30 |
| Observed state range | 18–35 | [FILL] |
| RarePct range | 52.9%–69.9% | [FILL] |
| NormalUnseen range | 0.0%–5.6% | [FILL] |
| P005 AbnUnseen | 8.3% | [FILL] |
| P011 AbnUnseen | 12.5% | [FILL] |

---

## Overall verdict

[FILL — is vC feasible? Does the network/resource dimension add independent anomaly signal beyond vA?]

*Generated by `MarkovTest/run.py` · Version C · Profile W60_S30 · 2026-06-18*
```

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/results/report_vC_W60_S30.csv MarkovTest/results/report_vC_W120_S60.csv MarkovTest/results/markov_vC_findings.md
git commit -m "data(markov): run Version C evaluation; add findings document"
```

---

## Task 7: Run Version D and write findings

**Files:**
- Create: `results/markov_vD_findings.md`

- [ ] **Step 1: Run vD on all participants, W60_S30**

```
cd MarkovTest && python run.py --version vD --profile W60_S30
```

Note the console output.

- [ ] **Step 2: Run vD on W120_S60**

```
cd MarkovTest && python run.py --version vD --profile W120_S60
```

- [ ] **Step 3: Create results/markov_vD_findings.md**

Fill all `[FILL]` placeholders from Step 1 console output:

```markdown
# Markov State Version D — Evaluation Findings

**Profile evaluated:** W60_S30
**Participants:** 15
**Total windows:** [FILL]
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
| [FILL] | |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| [FILL each row] | | | | | | |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant ≤ 14 (well below 17 theoretical max)

**Result:** [FILL — range X–Y]

**Verdict:** PASS/MARGINAL/FAIL. [Note if many participants collapse to UnknownPresence — this would indicate the presence sensor wasn't active for many sessions.]

### Criterion 2: RarePct < 60%

**Result:** [FILL]

**Verdict:** PASS/MARGINAL/FAIL.

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** [FILL]

**Verdict:** PASS/MARGINAL/FAIL.

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal |
|---|---|---|---|
| 001 | [FILL] | [FILL] | |
| 005 | [FILL] | [FILL] | |
| 006 | [FILL] | [FILL] | |
| 011 | [FILL] | [FILL] | |

**Verdict:** [FILL — does presence/display context reveal "out-of-physical-pattern" anomalies that vA's app/engagement view misses?]

---

## Comparison to Version A

| Metric | Version A | Version D |
|---|---|---|
| Theoretical max states | 53 | 17 |
| Observed state range | 18–35 | [FILL] |
| RarePct range | 52.9%–69.9% | [FILL] |
| NormalUnseen range | 0.0%–5.6% | [FILL] |
| P005 AbnUnseen | 8.3% | [FILL] |
| P011 AbnUnseen | 12.5% | [FILL] |

---

## Overall verdict

[FILL — is vD feasible? Does the physical-context dimension capture anomalous presence/display patterns that vA's engagement-mode misses? Are presence sensor gaps (UnknownPresence) a problem?]

*Generated by `MarkovTest/run.py` · Version D · Profile W60_S30 · 2026-06-18*
```

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/results/report_vD_W60_S30.csv MarkovTest/results/report_vD_W120_S60.csv MarkovTest/results/markov_vD_findings.md
git commit -m "data(markov): run Version D evaluation; add findings document"
```

---

## Self-Review

### Spec coverage

- ✅ Three new versions using completely different feature groups from vA
- ✅ vB: app-behavior features only (`app_top1_share`, `app_switches_per_active_min`)
- ✅ vC: network features + system resource features (6 new feature columns)
- ✅ vD: presence sensor + display state features (4 new feature columns, reuses `locked_ratio`)
- ✅ Unit tests for every sub-function and composite state in all three versions — boundary tests at every threshold
- ✅ `run.py` extended with `--version` flag; `analysis.py` and `report.py` unchanged
- ✅ Column rename trick (`state_col → markov_state_vA`) correctly gates analysis compatibility
- ✅ Findings templates for all three versions, format mirrors `markov_vA_findings.md`
- ✅ Both W60_S30 and W120_S60 profiles run in Tasks 5–7

### Placeholder scan

Tasks 5–7 contain `[FILL]` markers in the findings templates. These are **intentional** — the findings cannot be completed without running the code on real data. Every `[FILL]` is explicitly labeled with where to find the value (console output). No `[FILL]` markers appear in code blocks.

### Type consistency

- `assign_markov_state_vB/vC/vD(row: dict) -> str` — same signature as `assign_markov_state_vA`
- `run.py` calls `df.apply(assign_fn, axis=1)` — same pattern as the original vA runner
- `analysis_df = df.rename(columns={state_col: "markov_state_vA"})` — `analysis.py` and `report.py` call `build_participant_report(analysis_df, pid)`, which internally calls `transition_table(sub)` and `state_distribution(sub)`, both of which group on `"markov_state_vA"` — the rename ensures these calls work correctly
- `_row(**kwargs)` helpers in all test files follow the same pattern as `test_states.py`
