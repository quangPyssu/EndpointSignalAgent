# Feature Extractor - Quick Start Guide

## What Was Built

A complete **Feature Extractor** component with **SQLite-based storage** that runs in parallel with the existing signal processing pipeline. It reads signals, aggregates them in time windows, extracts features, stores them in a database, and automatically uploads them to the backend.

---

## Files Created

### Core Components
1. **`src/Contracts/FeatureRow.cs`**
   - Data structure for feature rows with database fields
   - Fields: `id`, `device_id`, `window_sec`, `window_start_ts`, `feature_version`, `features`, `sent_flag`, `sent_at`

2. **`src/State/FeatureStore.cs`**
   - Interface: `IFeatureStore`
   - Implementation: SQLite-based storage to `spool/features.db`
   - Methods: `StoreAsync()`, `GetUnsentAsync()`, `MarkAsSentAsync()`, `GetByIdsAsync()`, `GetRangeAsync()`, `GetLatestAsync()`, `DeleteOlderThanAsync()`

3. **`src/Configuration/FeatureExtractorOptions.cs`**
   - Configuration class for feature extraction settings
   - Loaded from `appsettings.json` ? `FeatureExtractor` section

4. **`src/Services/FeatureExtractorService.cs`**
   - Main BackgroundService that:
     - Consumes signals from shared channel
     - Buffers events in sliding time windows
     - Extracts features periodically
     - Stores to SQLite database

5. **`src/Contracts/FeatureBatchContracts.cs`**
   - DTOs for uploading features to backend
   - `FeatureBatchRequest` and `FeatureRowDto`

6. **`src/Services/FeatureUploadService.cs`**
   - Periodically uploads unsent features to backend
   - Marks uploaded features as sent
   - Includes retry logic with exponential backoff

7. **`src/Services/FeatureCleanupService.cs`**
   - Deletes old sent feature rows (7-day retention)
   - Runs daily to prevent database bloat

---

## Files Modified

### 1. `EndpointSignalAgent.csproj`
**Added:**
- `Microsoft.Data.Sqlite` NuGet package (v8.0.0)

### 2. `src/Program.cs`
**Changes:**
- Added `FeatureExtractorOptions` configuration with validation
- Changed signal channel from `SingleReader = true` to `SingleReader = false`
- Registered `IFeatureStore` / `FeatureStore` as singleton
- Registered `FeatureExtractorService` as hosted service
- Registered `FeatureUploadService` as hosted service
- Registered `FeatureCleanupService` as hosted service
- Added named HttpClient for feature upload

### 3. `src/Configuration/BackendOptions.cs`
**Added:**
- `FeaturesPath` property (default: `/features`)

### 4. `appsettings.json`
**Added:**
```json
"Backend": {
  "FeaturesPath": "/features"
},
"FeatureExtractor": {
  "Enabled": true,
  "WindowSizeSeconds": 60,
  "WindowSlideSeconds": 30,
  "MaxEventsPerWindow": 1000
}
```

---

## How It Works

### Signal Flow
```
Collectors ? Signal Channel ? [SignalWriterService, FeatureExtractorService]
                               ?                    ?
                               ?                    ?
                          spool/              spool/
                          signals.jsonl       features.db (SQLite)
                                                   ?
                                                   ?
                                            FeatureUploadService ? Backend
```

### Database Schema
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
```

### Feature Extraction Process

1. **Buffering:** Events are buffered as they arrive from the signal channel
2. **Windowing:** Every `WindowSlideSeconds`, a window is processed
3. **Extraction:** Features are computed from events in the window
4. **Storage:** Feature row is stored in SQLite with `sent_flag = 0`
5. **Upload:** FeatureUploadService periodically sends unsent rows to backend
6. **Marking:** Successfully uploaded rows are marked with `sent_flag = 1`, `sent_at = timestamp`
7. **Cleanup:** Old sent rows are deleted after retention period (7 days)

### Example Database Row
```json
{
  "id": 42,
  "device_id": "abc123",
  "window_sec": 60,
  "window_start_ts": "2024-01-15T10:00:00Z",
  "feature_version": "1.0",
  "features": {
    "event_count": 42,
    "unique_event_types": 5,
    "avg_interval_seconds": 1.43,
    "session_lock_count": 1,
    "app_switch_count": 12
  },
  "sent_flag": 1,
  "sent_at": "2024-01-15T10:02:30Z"
}
```

---

## Running the Feature Extractor

### Build & Run
```bash
dotnet restore
dotnet build
dotnet run
```

### Verify It's Working

1. **Check logs:**
   ```
   [FeatureExtractorService] Started for device {DeviceId}
   [FeatureExtractorService] Stored feature row 1 with 15 features
   [FeatureUploadService] Uploading 10 unsent feature rows
   [FeatureUploadService] Successfully uploaded and marked 10 feature rows as sent
   ```

2. **Check SQLite database:**
   ```bash
   sqlite3 spool/features.db
   SELECT COUNT(*) FROM feature_rows;
   SELECT * FROM feature_rows WHERE sent_flag = 0; -- Unsent rows
   SELECT * FROM feature_rows ORDER BY id DESC LIMIT 5; -- Latest rows
   ```

3. **Disable if needed:**
   In `appsettings.json`:
   ```json
   "FeatureExtractor": {
     "Enabled": false
   }
   ```

---

## Configuration Options

### FeatureExtractor Settings

| Setting | Default | Range | Description |
|---------|---------|-------|-------------|
| `Enabled` | `true` | boolean | Enable/disable feature extraction |
| `WindowSizeSeconds` | `60` | 10-3600 | Duration of aggregation window |
| `WindowSlideSeconds` | `30` | 5-3600 | How often to extract features |
| `MaxEventsPerWindow` | `1000` | 100-100,000 | Max events buffered in memory |

### Backend Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `FeaturesPath` | `/features` | Backend endpoint for uploading features |

### Upload & Cleanup Settings (Hardcoded)

| Setting | Default | Description |
|---------|---------|-------------|
| Upload Interval | 120 seconds | How often to upload unsent features |
| Batch Size | 50 rows | Max features per upload batch |
| Retention Period | 7 days | Delete sent features older than this |
| Cleanup Interval | 24 hours | How often to run cleanup |

---

## SQLite Database Management

### Query Examples

**Check unsent features:**
```sql
SELECT COUNT(*) FROM feature_rows WHERE sent_flag = 0;
```

**View recent features:**
```sql
SELECT 
    id, 
    device_id, 
    window_start_ts, 
    sent_flag, 
    sent_at 
FROM feature_rows 
ORDER BY id DESC 
LIMIT 10;
```

**Check sent features:**
```sql
SELECT COUNT(*) FROM feature_rows WHERE sent_flag = 1;
```

**Manually mark as unsent (for retry):**
```sql
UPDATE feature_rows 
SET sent_flag = 0, sent_at = NULL 
WHERE id = 42;
```

**View feature data:**
```sql
SELECT features_json FROM feature_rows WHERE id = 42;
```

### Database Maintenance

**Backup database:**
```bash
cp spool/features.db spool/features_backup.db
```

**Vacuum database (reclaim space after deletions):**
```bash
sqlite3 spool/features.db "VACUUM;"
```

**Export to JSON:**
```bash
sqlite3 spool/features.db ".mode json" "SELECT * FROM feature_rows;" > features_export.json
```

---

## Extracted Features (Base Implementation)

### Count Features
- `event_count` - Total events in window
- `unique_event_types` - Distinct event types
- `count_{EventType}` - Count per event type

### Time Features
- `avg_interval_seconds` - Average time between events
- `max_interval_seconds` - Maximum gap
- `min_interval_seconds` - Minimum gap

### Session Features
- `session_lock_count`
- `session_unlock_count`

### Application Features
- `app_switch_count` - Foreground app changes
- `unique_apps` - Distinct apps used

### Network Features
- `network_change_count`

### Display Features
- `display_on_count`
- `display_off_count`

---

## Extending the Feature Extractor

### Add Custom Features

Edit `src/Services/FeatureExtractorService.cs`, method `ExtractFeatures()`:

```csharp
private Dictionary<string, object> ExtractFeatures(...)
{
    var features = new Dictionary<string, object>();
    
    // Add your custom features here
    features["my_custom_metric"] = CalculateCustomMetric(events);
    
    return features;
}
```

### Change Storage Backend

Implement `IFeatureStore`:

```csharp
public class MyCustomStore : IFeatureStore
{
    public async Task StoreAsync(FeatureRow row, CancellationToken ct)
    {
        // Store to database, cloud, etc.
    }
    // ... implement other methods
}
```

Update `Program.cs`:
```csharp
builder.Services.AddSingleton<IFeatureStore, MyCustomStore>();
```

---

## Common Use Cases

### 1. Anomaly Detection
- Extract behavioral features
- Compare to baseline
- Flag unusual patterns

### 2. Risk Scoring
- Combine features into risk score
- Weight different behaviors
- Trigger actions on high risk

### 3. User Profiling
- Build behavioral profiles
- Track changes over time
- Identify user patterns

### 4. ML Training Data
- Export feature rows
- Train classification models
- Deploy inference pipeline

---

## Troubleshooting

### No Features Generated
- Check `"Enabled": true` in config
- Verify collectors are producing signals
- Check signal channel is not blocked
- Look for errors in logs

### High Memory Usage
- Reduce `MaxEventsPerWindow`
- Decrease `WindowSizeSeconds`
- Increase `WindowSlideSeconds`

### Features File Growing Too Large
- Implement file rotation
- Add compression
- Move to database storage

---

## Next Steps

1. **Define ML objectives:** What will these features be used for?
2. **Add domain features:** Implement application-specific feature engineering
3. **Integrate ML model:** Build inference pipeline consuming features
4. **Add monitoring:** Track feature quality metrics
5. **Optimize storage:** Implement archival and compression

---

## API Reference

### IFeatureStore Interface

```csharp
Task StoreAsync(FeatureRow featureRow, CancellationToken ct = default);
Task<List<FeatureRow>> GetRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
Task<List<FeatureRow>> GetLatestAsync(int count, CancellationToken ct = default);
```

### FeatureRow Contract

```csharp
public sealed record FeatureRow(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string DeviceId,
    Dictionary<string, object> Features
);
```

---

## Summary

? **Complete feature extraction pipeline** running in parallel with signal persistence  
? **Configurable time windows** for aggregation  
? **Base feature set** with 15+ features  
? **Extensible design** for custom features and storage  
? **Full documentation** of architecture and usage  

The Feature Extractor is now **ready to use** and can be extended based on your specific ML and analytics requirements!
