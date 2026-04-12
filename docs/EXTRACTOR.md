# Feature Extractor Component

## Overview

The **FeatureExtractorService** is a component that runs in parallel with the existing signal processing pipeline. It consumes signals from a dedicated broadcast channel and builds time-windowed feature rows for machine learning or analytics purposes. Features are stored in a **SQLite database** with automatic upload tracking and cleanup.

## Architecture

### Data Flow

```
┌─────────────────┐
│   Collectors    │ (Session, App, Network, SystemResource)
└─────────────────┘
         │ writes to
         ▼
┌─────────────────────────────┐
│    ISignalBroadcaster        │
│  (Broadcasts to 2 channels)  │
└─────────────────────────────┘
          │                   │
          ▼                   ▼
   ┌─────────────┐     ┌─────────────┐
   │  Channel #1 │     │  Channel #2 │
   │  (Writer)   │     │  (Extractor)│
   └─────────────┘     └─────────────┘
          │                   │
          ▼                   ▼
┌─────────────────┐   ┌───────────────────┐
│SignalWriterSvc  │   │ FeatureExtractorSvc│
│                 │   │                     │
│→ spool/         │   │→ Event-Time Windows│
│  signals.jsonl  │   │→ Feature Aggregation│
└─────────────────┘   │→ SQLite Storage    │
                      └───────────────────┘
                                 │
                                 ▼
                          ┌─────────────────┐
                          │  FeatureStore    │
                          │  (SQLite DB)     │
                          │→ spool/          │
                          │  features.db     │
                          └─────────────────┘
                                 │
                                 ▼
                    ┌────────────────────────┐
                    │  FeatureUploadService   │
                    │  (Auto-upload)          │
                    └────────────────────────┘
                                 │
                                 ▼
                    ┌────────────────────────┐
                    │ FeatureCleanupService   │
                    │ (7-day retention)       │
                    └────────────────────────┘
```

## Components

### 1. **FeatureExtractorService** (`src/FeatureExtraction/Services/FeatureExtractorService.cs`)

A `BackgroundService` that:
- Reads signals from a dedicated broadcast channel (separate from SignalWriterService)
- Uses **fixed event-time windows** (60s windows, 30s slide)
- Aggregates features using specialized aggregators (Session, App, Network, Cross, SystemResource)
- Stores feature rows to SQLite database via FeatureStore
- Supports both live extraction and on-demand extraction from files

**Key Configuration:**
- `Enabled`: Toggle to enable/disable the entire feature extraction system (default: true)
- `EnableLiveExtraction`: Toggle live extraction from broadcast channel (default: true)
- `WindowSizeSeconds`: Duration of the time window (fixed at 60s in current implementation)
- `WindowSlideSeconds`: How often to extract features (fixed at 30s in current implementation)
- `MaxEventsPerWindow`: Buffer size limit (default: 1000 events)

### 2. **FeatureStore** (`src/FeatureExtraction/Storage/FeatureStore.cs`)

Provides **SQLite-based persistent storage** for feature rows:
- **`StoreAsync()`**: Insert feature rows into SQLite database (returns auto-generated ID)
- **`GetUnsentAsync()`**: Query unsent features (sent_flag = 0) for upload
- **`MarkAsSentAsync()`**: Mark features as uploaded (sent_flag = 1, sent_at = now)
- **`GetByIdsAsync()`**: Get specific feature rows by ID
- **`GetRangeAsync()`**: Query features by time range
- **`GetLatestAsync()`**: Retrieve the most recent N feature rows
- **`DeleteOlderThanAsync()`**: Delete old sent features (for cleanup)
- **`GetAllAsync()`**: Get all feature rows (sent and unsent)

### 3. **FeatureRow** (`src/FeatureExtraction/Contracts/FeatureRow.cs`)

Data structure representing a feature row (SQLite-based):
```csharp
public sealed record FeatureRow(
    long Id,                        // Database primary key (auto-generated)
    string DeviceId,                // Device identifier
    int WindowSec,                  // Window size in seconds (60)
    DateTimeOffset WindowStartTs,   // Window start timestamp
    string FeatureVersion,          // Schema version ("1.0")
    Dictionary<string, object> Features,  // Feature dictionary
    bool SentFlag,                  // Upload tracking (0 = unsent, 1 = sent)
    DateTimeOffset? SentAt          // Upload timestamp
);
```

**Note:** The `WindowEnd` can be calculated as `WindowStartTs + TimeSpan.FromSeconds(WindowSec)`.

### 4. **FeatureExtractorOptions** (`src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs`)

Configuration options loaded from `appsettings.json`:
```json
"FeatureExtractor": {
  "Enabled": true,
  "EnableLiveExtraction": true,
  "WindowSizeSeconds": 60,
  "WindowSlideSeconds": 30,
  "MaxEventsPerWindow": 1000
}
```

### 5. **FeatureUploadService** (`src/FeatureExtraction/Services/FeatureUploadService.cs`)

A `BackgroundService` that automatically uploads unsent features to the backend:
- Runs every **2 minutes**
- Uploads up to **50 unsent rows** per batch
- Marks uploaded features as sent in the database
- Uses exponential backoff retry (5s → 60s max) on failures
- Endpoint: `POST {BaseUrl}/features` (configurable via `BackendOptions.FeaturesPath`)

### 6. **FeatureCleanupService** (`src/FeatureExtraction/Services/FeatureCleanupService.cs`)

A `BackgroundService` that automatically deletes old sent features:
- Runs **daily**
- Deletes sent features older than **7 days**
- Prevents database bloat while retaining recent data

### 7. **KeyboardCommandService** (`src/FeatureExtraction/Services/KeyboardCommandService.cs`)

A `BackgroundService` that monitors keyboard commands for administrative tasks:
- **Ctrl+E**: Extract features from all signals in `spool/signals.jsonl` (on-demand extraction)
- **Ctrl+P**: Export unsent features to CSV
- **Ctrl+O**: Export all features to CSV
- **Ctrl+Shift+X**: Clear all feature data from database

## Extracted Features

The current base implementation extracts the following features per time window:

### Count-Based Features
- `event_count`: Total number of events in the window
- `unique_event_types`: Number of distinct event types
- `count_{EventType}`: Count for each specific event type

### Time-Based Features
- `avg_interval_seconds`: Average time between consecutive events
- `max_interval_seconds`: Maximum time gap between events
- `min_interval_seconds`: Minimum time gap between events

### Session State Features
- `session_lock_count`: Number of session lock events
- `session_unlock_count`: Number of session unlock events

### Application Usage Features
- `app_switch_count`: Number of foreground app changes
- `unique_apps`: Number of distinct applications used

### Network Context Features
- `network_change_count`: Number of network connectivity changes

### Display State Features
- `display_on_count`: Number of display-on events
- `display_off_count`: Number of display-off events

### System Resource Features
- CPU: `cpu_usage_mean`, `cpu_usage_max`, `cpu_usage_std`, `cpu_usage_high_ratio`, `cpu_spike_count`
- RAM: `ram_usage_mean`, `ram_usage_max`, `ram_usage_std`, `ram_high_usage_ratio`, `ram_pressure_events`
- GPU: `gpu_available`, `gpu_usage_mean`, `gpu_usage_max`, `gpu_usage_std`, `gpu_memory_usage_mean`, `gpu_high_usage_ratio`
- Network throughput: `net_bytes_sent_mean`, `net_bytes_recv_mean`, `net_bytes_total_mean`, `net_bytes_total_max`, `net_activity_ratio`, `net_throughput_std`, `net_spike_count`
- Cross-resource: `system_load_index`, `resource_variability_index`, `cpu_ram_correlation_proxy`, `active_resource_ratio`, `has_system_data`

## Key Changes to Existing Architecture

### Signal Channel Configuration
**Before:**
```csharp
// Single channel with SingleReader = true
var signalChannel = Channel.CreateBounded<BroadcastSignal>(...);
```

**After:**
```csharp
// Two separate channels with broadcast pattern
var signalWriterChannel = Channel.CreateBounded<BroadcastSignal>(...);
var featureExtractorChannel = Channel.CreateBounded<BroadcastSignal>(...);

// SignalBroadcaster writes to both channels
builder.Services.AddSingleton<ISignalBroadcaster>(sp =>
{
    var writers = new[] { signalWriterChannel.Writer, featureExtractorChannel.Writer };
    return new SignalBroadcaster(logger, writers);
});
```

This change is in `Program.cs` and implements a **broadcast pattern** where collectors write once to the broadcaster, which then writes to multiple independent channels. Each channel has `SingleReader = true` for its dedicated consumer.

## Extension Points

### Custom Feature Extraction

To add domain-specific features, modify the `ExtractFeatures()` method in `FeatureExtractorService.cs`:

```csharp
private Dictionary<string, object> ExtractFeatures(
    List<(DateTimeOffset Timestamp, SignalEventType Type, 
          Dictionary<string, string> Payload)> events)
{
    var features = new Dictionary<string, object>();
    
    // Add your custom feature extraction logic here
    // Examples:
    // - Behavioral patterns (typing speed, mouse patterns)
    // - Context switches (rapid app switching)
    // - Anomaly detection (unusual activity times)
    // - Temporal patterns (time of day, day of week)
    
    return features;
}
```

### Custom Storage Backend

The current implementation uses SQLite. To use a different storage backend (e.g., PostgreSQL, cloud storage), implement `IFeatureStore`:

```csharp
public class PostgresFeatureStore : IFeatureStore
{
    public async Task<long> StoreAsync(FeatureRow featureRow, CancellationToken ct)
    {
        // Store to PostgreSQL and return ID
        return insertedId;
    }

    // ... implement other interface methods
}
```

Then update `Program.cs`:
```csharp
builder.Services.AddSingleton<IFeatureStore, PostgresFeatureStore>();
```

## Testing

The component starts automatically with the service. To verify it's working:

1. **Check Logs:**
   ```
   [FeatureExtractorService] Started for device {DeviceId} with fixed event-time windows 60s/30s
   [FeatureExtractorService] Stored feature row 42 with 15 features for window 2024-01-15T10:00:00Z
   [FeatureUploadService] Successfully uploaded and marked 10 feature rows as sent
   [FeatureCleanupService] Deleted 120 old feature rows (cutoff: 2024-01-08T10:00:00Z)
   ```

2. **Inspect Feature Store (SQLite):**
   ```bash
   sqlite3 spool/features.db "SELECT COUNT(*) FROM feature_rows;"
   sqlite3 spool/features.db "SELECT * FROM feature_rows WHERE sent_flag = 0 LIMIT 5;"
   ```

3. **Use Keyboard Commands:**
   - Press **Ctrl+E** to extract features from all signals on-demand
   - Press **Ctrl+P** to export unsent features to CSV
   - Press **Ctrl+O** to export all features to CSV

4. **Disable if Needed:**
   Set in `appsettings.json`:
   ```json
   "FeatureExtractor": {
     "Enabled": false
   }
   ```
   Or to disable only live extraction (keeping on-demand via Ctrl+E):
   ```json
   "FeatureExtractor": {
     "Enabled": true,
     "EnableLiveExtraction": false
   }
   ```

## Performance Considerations

- **Memory:** The service buffers up to `MaxEventsPerWindow` events in memory per window
- **I/O:** SQLite writes are transactional; feature rows are inserted every 30 seconds (slide interval)
- **Database Size:** ~500 bytes to 2 KB per row; with 7-day retention at 1 feature/minute ≈ 10 MB
- **CPU:** Feature extraction is CPU-bound; uses specialized aggregators for efficiency
- **Channel Backpressure:** Each broadcast channel uses `FullMode.Wait`, so if any consumer is slow, collectors will block
- **Upload Frequency:** Features are uploaded every 2 minutes (up to 50 rows per batch)
- **Cleanup Frequency:** Old features are deleted daily

## Future Enhancements

1. **Real-time ML Inference:** Feed extracted features to an ML model for anomaly detection
2. **Feature Store API:** Expose REST endpoints to query features
3. **Advanced Features:** Sequential patterns, context embeddings, behavioral biometrics
4. **Distributed Processing:** Scale feature extraction across multiple workers
5. **Configurable Retention:** Make the 7-day retention period configurable
6. **Feature Versioning:** Support multiple feature schema versions simultaneously
7. **Cloud Storage:** Alternative backends for cloud-based feature storage

## Files Created

### Core Feature Extraction
- `src/FeatureExtraction/Contracts/FeatureRow.cs` - Feature row data model (SQLite-based)
- `src/FeatureExtraction/Contracts/FeatureBatchContracts.cs` - Upload DTOs
- `src/FeatureExtraction/Storage/FeatureStore.cs` - SQLite-based feature storage
- `src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs` - Configuration options
- `src/FeatureExtraction/Services/FeatureExtractorService.cs` - Main extraction service
- `src/FeatureExtraction/Services/FeatureUploadService.cs` - Automatic upload service
- `src/FeatureExtraction/Services/FeatureCleanupService.cs` - Automatic cleanup service
- `src/FeatureExtraction/Services/KeyboardCommandService.cs` - Admin commands

### Feature Aggregators
- `src/FeatureExtraction/SignalAggregator/AppFeatureAggregator.cs` - Application usage features
- `src/FeatureExtraction/SignalAggregator/SessionFeatureAggregator.cs` - Session state features
- `src/FeatureExtraction/SignalAggregator/NetworkFeatureAggregator.cs` - Network context features
- `src/FeatureExtraction/SignalAggregator/CrossFeatureAggregator.cs` - Cross-domain features

### Broadcasting Infrastructure
- `src/SignalCollection/Broadcasting/ISignalBroadcaster.cs` - Broadcaster interface
- `src/SignalCollection/Broadcasting/SignalBroadcaster.cs` - Broadcast implementation
- `src/SignalCollection/Broadcasting/BroadcastSignal.cs` - Signal record type
- `src/SignalCollection/Broadcasting/ISignalWriterChannelReader.cs` - Writer channel reader
- `src/FeatureExtraction/Broadcasting/IFeatureExtractorChannelReader.cs` - Extractor channel reader

## Files Modified

- `src/Program.cs` - Added broadcast pattern with two separate channels, registered all feature services
- `src/Bootstrap/Configuration/BackendOptions.cs` - Added `FeaturesPath` property
- `appsettings.json` - Added FeatureExtractor configuration section and FeaturesPath
- `EndpointSignalAgent.csproj` - Added `Microsoft.Data.Sqlite` package reference
- All collectors - Updated to use `ISignalBroadcaster` instead of direct channel writes

## Database Schema

The SQLite database (`spool/features.db`) uses the following schema:

```sql
CREATE TABLE feature_rows (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT NOT NULL,
    window_sec INTEGER NOT NULL,
    window_start_ts TEXT NOT NULL,
    feature_version TEXT NOT NULL,
    features_json TEXT NOT NULL,
    sent_flag INTEGER NOT NULL DEFAULT 0,
    sent_at TEXT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_sent_flag ON feature_rows(sent_flag);
CREATE INDEX idx_window_start ON feature_rows(window_start_ts);
CREATE INDEX idx_device_id ON feature_rows(device_id);
```

## Related Documentation

- **[SQLite-Migration.md](SQLite-Migration.md)** - Detailed documentation of the SQLite migration
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Overall system architecture including broadcast pattern
