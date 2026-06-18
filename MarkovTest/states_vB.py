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
