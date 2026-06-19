import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vW import assign_markov_state_vW


def _row(**kwargs):
    defaults = dict(
        has_app_data=1,
        cat_browser_ratio=0.0, cat_ide_ratio=0.0, cat_terminal_ratio=0.0,
        cat_comms_ratio=0.0, cat_office_ratio=0.0, cat_media_ratio=0.0,
        cat_gaming_ratio=0.0, cat_remoteaccess_ratio=0.0,
        cat_filemanager_ratio=0.0, cat_system_ratio=0.0, cat_other_ratio=0.0,
        locked_ratio=0.0, active_work_ratio=0.0,
        idle_bucket_mean_sec=0.0, idle_ge_300_ratio=0.0, has_idle_data=1,
    )
    defaults.update(kwargs)
    return defaults


def test_vW_no_app():
    assert assign_markov_state_vW(_row(has_app_data=0)) == "NoApp"

def test_vW_browser():
    assert assign_markov_state_vW(_row(cat_browser_ratio=0.65)) == "BrowserWork"

def test_vW_developer():
    assert assign_markov_state_vW(_row(cat_ide_ratio=0.30, cat_terminal_ratio=0.25)) == "DeveloperWork"

def test_vW_mixed():
    assert assign_markov_state_vW(_row(cat_browser_ratio=0.30, cat_comms_ratio=0.30)) == "MixedWork"

def test_vW_other():
    assert assign_markov_state_vW(_row(cat_other_ratio=0.60)) == "OtherWork"

def test_vW_system():
    assert assign_markov_state_vW(_row(cat_system_ratio=0.55)) == "SystemWork"

def test_vW_remote_access_priority_over_developer():
    r = _row(cat_remoteaccess_ratio=0.35, cat_ide_ratio=0.40, cat_terminal_ratio=0.20)
    assert assign_markov_state_vW(r) == "RemoteAccessWork"
