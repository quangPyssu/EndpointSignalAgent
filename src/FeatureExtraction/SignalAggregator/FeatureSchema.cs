namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

internal static class FeatureSchema
{
    public const string FeatureVersion = "1.2";
    public const int WindowSec = 60;
    public const int StepSec = 30;

    public static readonly string[] AppColumns =
    {
        "app_switch_count",
        "app_unique_count",
        "app_dwell_mean_ms",
        "app_dwell_std_ms",
        "app_dwell_max_ms",
        "app_top1_share",
        "cat_browser_ratio",
        "cat_ide_ratio",
        "cat_terminal_ratio",
        "cat_comms_ratio",
        "cat_office_ratio",
        "cat_media_ratio",
        "cat_design_ratio",
        "cat_database_ratio",
        "cat_gaming_ratio",
        "cat_remoteaccess_ratio",
        "cat_filemanager_ratio",
        "cat_email_ratio",
        "cat_system_ratio",
        "cat_other_ratio",
        "app_confidence_high_ratio",
        "no_foreground_end_count",
        "collector_mode_hook_ratio",
        "has_app_data"
    };

    public static readonly string[] SessionColumns =
    {
        "lock_count",
        "unlock_count",
        "display_toggle_count",
        "screensaver_toggle_count",
        "locked_ratio",
        "display_off_ratio",
        "display_dim_ratio",
        "display_on_ratio",
        "screensaver_on_ratio",
        "presence_away_ratio",
        "presence_present_ratio",
        "idle_bucket_mean_sec",
        "idle_bucket_max_sec",
        "idle_ge_60_ratio",
        "idle_ge_300_ratio",
        "has_idle_data",
        "has_display_data",
        "idle_api_fail_count",
        "presence_available_ratio"
    };

    public static readonly string[] NetworkColumns =
    {
        "vpn_on_ratio",
        "primary_wifi_connected_ratio",
        "public_ip_known_ratio",
        "vpn_flip_count",
        "wifi_flip_count",
        "ssid_change_count",
        "local_network_change_count",
        "public_ip_bucket_change_count",
        "unique_wifi_ssid_count",
        "unique_wifi_bssid_count",
        "unique_local_network_count",
        "unique_public_bucket_count",        "public_ip_fetch_fail_count",
        "public_ip_backoff_ratio",
        "has_net_data",
        // Compatibility aliases.
        "wifi_up_ratio",
        "local_prefix_change_count"
    };

    public static readonly string[] CrossColumns =
    {
        "active_work_ratio",
        "app_switches_per_active_min",
        "category_entropy_active"
    };

    public static readonly string[] SystemColumns =
    {
        "cpu_usage_mean",
        "cpu_usage_max",
        "cpu_usage_std",
        "cpu_usage_high_ratio",
        "cpu_spike_count",
        "ram_usage_mean",
        "ram_usage_max",
        "ram_usage_std",
        "ram_high_usage_ratio",
        "ram_pressure_events",
        "gpu_available",
        "gpu_usage_mean",
        "gpu_usage_max",
        "gpu_usage_std",
        "gpu_memory_usage_mean",
        "gpu_high_usage_ratio",
        "net_bytes_sent_mean",
        "net_bytes_recv_mean",
        "net_bytes_total_mean",
        "net_bytes_total_max",
        "net_activity_ratio",
        "net_throughput_std",
        "net_spike_count",
        "system_load_index",
        "resource_variability_index",
        "cpu_ram_correlation_proxy",
        "active_resource_ratio",
        "has_system_data"
    };

    public static readonly string[] AllColumns = AppColumns
        .Concat(SessionColumns)
        .Concat(NetworkColumns)
        .Concat(CrossColumns)
        .Concat(SystemColumns)
        .ToArray();

    public static readonly IReadOnlyDictionary<string, string> CategoryToColumn = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["browser"] = "cat_browser_ratio",
        ["ide"] = "cat_ide_ratio",
        ["terminal"] = "cat_terminal_ratio",
        ["comms"] = "cat_comms_ratio",
        ["office"] = "cat_office_ratio",
        ["media"] = "cat_media_ratio",
        ["design"] = "cat_design_ratio",
        ["database"] = "cat_database_ratio",
        ["gaming"] = "cat_gaming_ratio",
        ["remoteaccess"] = "cat_remoteaccess_ratio",
        ["filemanager"] = "cat_filemanager_ratio",
        ["email"] = "cat_email_ratio",
        ["system"] = "cat_system_ratio",
        ["other"] = "cat_other_ratio"
    };
}
