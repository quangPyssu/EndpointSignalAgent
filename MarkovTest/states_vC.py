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
