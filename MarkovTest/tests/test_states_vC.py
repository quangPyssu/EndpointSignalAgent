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
