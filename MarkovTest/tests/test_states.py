import pytest
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states import assign_work_mode, assign_engagement_mode, assign_markov_state_vA


# ── WorkMode ─────────────────────────────────────────────────────────────────

def _row(**kwargs):
    """Build a minimal feature row with safe zero defaults."""
    defaults = dict(
        has_app_data=1,
        cat_browser_ratio=0, cat_ide_ratio=0, cat_terminal_ratio=0,
        cat_comms_ratio=0, cat_office_ratio=0, cat_media_ratio=0,
        cat_gaming_ratio=0, cat_remoteaccess_ratio=0,
        cat_filemanager_ratio=0, cat_system_ratio=0, cat_other_ratio=0,
        locked_ratio=0, active_work_ratio=0,
        idle_bucket_mean_sec=0, idle_ge_300_ratio=0, has_idle_data=1,
    )
    defaults.update(kwargs)
    return defaults


def test_work_mode_no_app():
    assert assign_work_mode(_row(has_app_data=0)) == "NoApp"


def test_work_mode_remote_access_priority():
    r = _row(cat_remoteaccess_ratio=0.35, cat_ide_ratio=0.40, cat_terminal_ratio=0.20)
    assert assign_work_mode(r) == "RemoteAccessWork"


def test_work_mode_developer_ide_terminal_combined():
    r = _row(cat_ide_ratio=0.30, cat_terminal_ratio=0.25)  # sum = 0.55
    assert assign_work_mode(r) == "DeveloperWork"


def test_work_mode_terminal_only():
    r = _row(cat_terminal_ratio=0.45, cat_ide_ratio=0.05)  # dev_ratio=0.50 exactly → DeveloperWork
    assert assign_work_mode(r) == "DeveloperWork"

def test_work_mode_terminal_alone():
    r = _row(cat_terminal_ratio=0.42, cat_ide_ratio=0.02)  # dev_ratio=0.44 < 0.50 → TerminalWork
    assert assign_work_mode(r) == "TerminalWork"


def test_work_mode_dominant_browser():
    r = _row(cat_browser_ratio=0.65)
    assert assign_work_mode(r) == "BrowserWork"


def test_work_mode_dominant_comms():
    r = _row(cat_comms_ratio=0.55)
    assert assign_work_mode(r) == "CommsWork"


def test_work_mode_dominant_office():
    r = _row(cat_office_ratio=0.60)
    assert assign_work_mode(r) == "OfficeWork"


def test_work_mode_dominant_media():
    r = _row(cat_media_ratio=0.70)
    assert assign_work_mode(r) == "MediaWork"


def test_work_mode_dominant_gaming():
    r = _row(cat_gaming_ratio=0.55)
    assert assign_work_mode(r) == "GamingWork"


def test_work_mode_dominant_file():
    r = _row(cat_filemanager_ratio=0.52)
    assert assign_work_mode(r) == "FileWork"


def test_work_mode_dominant_system():
    r = _row(cat_system_ratio=0.51)
    assert assign_work_mode(r) == "SystemWork"


def test_work_mode_dominant_other():
    r = _row(cat_other_ratio=0.55)
    assert assign_work_mode(r) == "OtherWork"


def test_work_mode_mixed_no_dominant():
    r = _row(cat_browser_ratio=0.30, cat_comms_ratio=0.30, cat_office_ratio=0.20)
    assert assign_work_mode(r) == "MixedWork"


def test_work_mode_exact_thresholds_remote_access():
    r = _row(cat_remoteaccess_ratio=0.30)
    assert assign_work_mode(r) == "RemoteAccessWork"


def test_work_mode_remote_access_below_threshold():
    r = _row(cat_remoteaccess_ratio=0.29, cat_browser_ratio=0.55)
    assert assign_work_mode(r) == "BrowserWork"


# ── EngagementMode ────────────────────────────────────────────────────────────

def test_engagement_locked():
    r = _row(locked_ratio=0.80)
    assert assign_engagement_mode(r) == "Locked"


def test_engagement_locked_exact_threshold():
    r = _row(locked_ratio=0.80)
    assert assign_engagement_mode(r) == "Locked"


def test_engagement_locked_below_threshold():
    r = _row(locked_ratio=0.79, has_idle_data=1, idle_ge_300_ratio=0, idle_bucket_mean_sec=10, active_work_ratio=0.6)
    assert assign_engagement_mode(r) == "Active"


def test_engagement_no_idle_data_active():
    r = _row(has_idle_data=0, active_work_ratio=0.75, locked_ratio=0)
    assert assign_engagement_mode(r) == "Active"


def test_engagement_no_idle_data_unknown():
    r = _row(has_idle_data=0, active_work_ratio=0.50, locked_ratio=0)
    assert assign_engagement_mode(r) == "UnknownEngagement"


def test_engagement_long_idle():
    r = _row(idle_ge_300_ratio=0.50, idle_bucket_mean_sec=400, locked_ratio=0)
    assert assign_engagement_mode(r) == "LongIdle"


def test_engagement_light_idle_mean():
    r = _row(idle_ge_300_ratio=0.10, idle_bucket_mean_sec=90, locked_ratio=0)
    assert assign_engagement_mode(r) == "LightIdle"


def test_engagement_active_via_active_work_ratio():
    r = _row(idle_ge_300_ratio=0.05, idle_bucket_mean_sec=30, active_work_ratio=0.55, locked_ratio=0)
    assert assign_engagement_mode(r) == "Active"


def test_engagement_light_idle_fallback():
    r = _row(idle_ge_300_ratio=0.05, idle_bucket_mean_sec=30, active_work_ratio=0.40, locked_ratio=0)
    assert assign_engagement_mode(r) == "LightIdle"


# ── assign_markov_state_vA ────────────────────────────────────────────────────

def test_state_locked_collapses():
    r = _row(locked_ratio=0.90)
    assert assign_markov_state_vA(r) == "Locked"


def test_state_noapp_light_idle():
    r = _row(has_app_data=0, has_idle_data=1, idle_ge_300_ratio=0, idle_bucket_mean_sec=90, locked_ratio=0)
    assert assign_markov_state_vA(r) == "NoApp_LightIdle"


def test_state_noapp_long_idle():
    r = _row(has_app_data=0, has_idle_data=1, idle_ge_300_ratio=0.60, locked_ratio=0)
    assert assign_markov_state_vA(r) == "NoApp_LongIdle"


def test_state_browser_active():
    r = _row(cat_browser_ratio=0.70, active_work_ratio=0.60,
             idle_bucket_mean_sec=20, idle_ge_300_ratio=0, locked_ratio=0)
    assert assign_markov_state_vA(r) == "BrowserWork_Active"


def test_state_developer_light_idle():
    r = _row(cat_ide_ratio=0.35, cat_terminal_ratio=0.25,
             idle_bucket_mean_sec=120, idle_ge_300_ratio=0.10,
             active_work_ratio=0.30, locked_ratio=0)
    assert assign_markov_state_vA(r) == "DeveloperWork_LightIdle"


def test_state_unknown_engagement():
    r = _row(has_app_data=0, has_idle_data=0, active_work_ratio=0.40, locked_ratio=0)
    assert assign_markov_state_vA(r) == "NoApp_UnknownEngagement"
