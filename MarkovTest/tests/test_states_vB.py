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
