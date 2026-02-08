# Feature Extractor Component

## Overview

The **FeatureExtractorService** is a new component that runs in parallel with the existing signal processing pipeline. It consumes signals from the shared signal channel and builds time-windowed feature rows for machine learning or analytics purposes.

## Architecture

### Data Flow

```
???????????????????
?   Collectors    ? (Session, App, Network)
???????????????????
         ? writes to
         ?
???????????????????????????????????????
?      Signal Channel (Shared)        ?
?  (Multi-Reader: SignalWriter +      ?
?   FeatureExtractor)                 ?
???????????????????????????????????????
          ?                   ?
          ?                   ?
???????????????????   ???????????????????????
?SignalWriterSvc  ?   ? FeatureExtractorSvc ?
?                 ?   ?                     ?
?? spool/         ?   ?? Window Aggregation ?
?  signals.jsonl  ?   ?? Feature Extraction ?
???????????????????   ?? FeatureStore       ?
                      ???????????????????????
                                 ?
                                 ?
                          ???????????????
                          ?FeatureStore ?
                          ?             ?
                          ?? spool/     ?
                          ?  features.  ?
                          ?  jsonl      ?
                          ???????????????
```

## Components

### 1. **FeatureExtractorService** (`src/Services/FeatureExtractorService.cs`)

A `BackgroundService` that:
- Reads signals from the shared signal channel (parallel to SignalWriterService)
- Buffers signals in a sliding time window
- Periodically extracts features from the buffered events
- Stores feature rows to the FeatureStore

**Key Configuration:**
- `WindowSizeSeconds`: Duration of the time window (default: 60s)
- `WindowSlideSeconds`: How often to extract features (default: 30s)
- `MaxEventsPerWindow`: Buffer size limit (default: 1000 events)
- `Enabled`: Toggle to enable/disable feature extraction (default: true)

### 2. **FeatureStore** (`src/State/FeatureStore.cs`)

Provides persistent storage for feature rows:
- **`StoreAsync()`**: Append feature rows to `spool/features.jsonl`
- **`GetRangeAsync()`**: Query features by time range
- **`GetLatestAsync()`**: Retrieve the most recent N feature rows

### 3. **FeatureRow** (`src/Contracts/FeatureRow.cs`)

Data structure representing a feature row:
```csharp
public sealed record FeatureRow(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string DeviceId,
    Dictionary<string, object> Features
);
```

### 4. **FeatureExtractorOptions** (`src/Configuration/FeatureExtractorOptions.cs`)

Configuration options loaded from `appsettings.json`:
```json
"FeatureExtractor": {
  "Enabled": true,
  "WindowSizeSeconds": 60,
  "WindowSlideSeconds": 30,
  "MaxEventsPerWindow": 1000
}
```

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

## Key Changes to Existing Architecture

### Signal Channel Configuration
**Before:**
```csharp
SingleReader = true  // Only SignalWriterService consumed signals
```

**After:**
```csharp
SingleReader = false // Both SignalWriterService and FeatureExtractorService consume
```

This change is in `Program.cs` and allows parallel consumption of the signal stream.

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

To replace the file-based storage, implement `IFeatureStore`:

```csharp
public class DatabaseFeatureStore : IFeatureStore
{
    public async Task StoreAsync(FeatureRow featureRow, CancellationToken ct)
    {
        // Store to database/cloud/etc.
    }
    
    // ... implement other interface methods
}
```

Then update `Program.cs`:
```csharp
builder.Services.AddSingleton<IFeatureStore, DatabaseFeatureStore>();
```

## Testing

The component starts automatically with the service. To verify it's working:

1. **Check Logs:**
   ```
   [FeatureExtractorService] Started for device {DeviceId}
   [FeatureExtractorService] Extracting features from N events in window...
   [FeatureExtractorService] Stored feature row with M features
   ```

2. **Inspect Feature Store:**
   ```
   spool/features.jsonl
   ```
   Each line is a JSON-serialized `FeatureRow`.

3. **Disable if Needed:**
   Set in `appsettings.json`:
   ```json
   "FeatureExtractor": {
     "Enabled": false
   }
   ```

## Performance Considerations

- **Memory:** The service buffers up to `MaxEventsPerWindow` events in memory
- **I/O:** Feature rows are appended to disk every `WindowSlideSeconds`
- **CPU:** Feature extraction is CPU-bound; adjust window size accordingly
- **Channel Backpressure:** The signal channel uses `FullMode.Wait`, so if both consumers are slow, collectors will block

## Future Enhancements

1. **Real-time ML Inference:** Feed extracted features to an ML model for anomaly detection
2. **Feature Store API:** Expose REST endpoints to query features
3. **Feature Compression:** Archive old features to reduce disk usage
4. **Advanced Features:** Sequential patterns, context embeddings, behavioral biometrics
5. **Distributed Processing:** Scale feature extraction across multiple workers

## Files Created

- `src/Contracts/FeatureRow.cs`
- `src/State/FeatureStore.cs`
- `src/Configuration/FeatureExtractorOptions.cs`
- `src/Services/FeatureExtractorService.cs`

## Files Modified

- `src/Program.cs` - Added FeatureExtractor registration and changed channel to multi-reader
- `appsettings.json` - Added FeatureExtractor configuration section
