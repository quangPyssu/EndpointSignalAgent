def assign_work_mode(row):
    if row.get("has_app_data", 0) == 0:
        return "NoApp"

    dev_ratio = row.get("cat_ide_ratio", 0) + row.get("cat_terminal_ratio", 0)

    if row.get("cat_remoteaccess_ratio", 0) >= 0.30:
        return "RemoteAccessWork"

    if dev_ratio >= 0.50:
        return "DeveloperWork"

    if row.get("cat_terminal_ratio", 0) >= 0.40:
        return "TerminalWork"

    main_ratios = {
        "BrowserWork": row.get("cat_browser_ratio", 0),
        "CommsWork": row.get("cat_comms_ratio", 0),
        "OfficeWork": row.get("cat_office_ratio", 0),
        "MediaWork": row.get("cat_media_ratio", 0),
        "GamingWork": row.get("cat_gaming_ratio", 0),
        "FileWork": row.get("cat_filemanager_ratio", 0),
        "SystemWork": row.get("cat_system_ratio", 0),
        "OtherWork": row.get("cat_other_ratio", 0),
    }

    best_mode = max(main_ratios, key=main_ratios.get)
    best_ratio = main_ratios[best_mode]

    if best_ratio >= 0.50:
        return best_mode

    return "MixedWork"


def assign_engagement_mode(row):
    if row.get("locked_ratio", 0) >= 0.80:
        return "Locked"

    if row.get("has_idle_data", 0) == 0:
        if row.get("active_work_ratio", 0) >= 0.70:
            return "Active"
        return "UnknownEngagement"

    if row.get("idle_ge_300_ratio", 0) >= 0.50:
        return "LongIdle"

    if row.get("idle_bucket_mean_sec", 0) >= 60:
        return "LightIdle"

    if row.get("active_work_ratio", 0) >= 0.50:
        return "Active"

    return "LightIdle"


def assign_markov_state_vA(row):
    work = assign_work_mode(row)
    engage = assign_engagement_mode(row)

    if engage == "Locked":
        return "Locked"

    if work == "NoApp" and engage in ("LongIdle", "LightIdle", "UnknownEngagement"):
        return f"NoApp_{engage}"

    return f"{work}_{engage}"
