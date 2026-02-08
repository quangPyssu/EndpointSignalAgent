# EndpointSignalAgent - Updated Architecture (with Feature Extractor)

## System Overview

The EndpointSignalAgent now has **dual-path signal processing**:
1. **Signal Persistence Path**: Collectors ? Signal Channel ? SignalWriterService ? Spool File
2. **Feature Extraction Path**: Collectors ? Signal Channel ? FeatureExtractorService ? Feature Store

Both paths consume from the **same shared channel** in parallel.

---

## Updated Data Flow Diagram

```
?????????????????????????????????????????????????????????????????
?                        COLLECTORS                             ?
?  SessionStateCollector | ApplicationUsageCollector           ?
?  NetworkContextCollector                                      ?
?????????????????????????????????????????????????????????????????
                          ? writes signals to
                          ?
        ???????????????????????????????????????????
        ?      Signal Channel (Multi-Reader)       ?
        ?   Bounded: 1000 events, Wait on full    ?
        ????????????????????????????????????????????
                  ?                  ?
        ??????????????????  ????????????????????????????
        ?SignalWriterSvc ?  ?FeatureExtractorService   ?
        ?                ?  ?                          ?
        ?• Persists raw  ?  ?• Buffers in time window  ?
        ?  signals       ?  ?• Extracts features       ?
        ??????????????????  ?• Stores to SQLite        ?
                 ?          ????????????????????????????
                 ?                  ?
                 ?                  ?
        ??????????????????  ??????????????????????????
        ?spool/          ?  ?spool/features.db       ?
        ? signals.jsonl  ?  ? (SQLite Database)      ?
        ??????????????????  ?• sent_flag tracking    ?
                 ?          ?• Upload management     ?
                 ?          ??????????????????????????
                 ?                   ?
                 ? read by           ? read by
                 ?                   ?
        ??????????????????  ???????????????????????
        ?SpoolFileProvider?  ?FeatureUploadService ?
        ??????????????????  ?                     ?
                 ?          ?• Gets unsent rows   ?
                 ?          ?• Uploads to backend ?
                 ?          ?• Marks as sent      ?
                 ?          ???????????????????????
                 ?                     ?
                 ?                     ? HTTP
        ?????????????????????         ?
        ?BatchProducerService??????   ?
        ?????????????????????    ?   ?
                                 ?   ?
                                 ?   ?
????????????????         ????????????????
?DecisionQueue ?         ?   Backend    ?
????????????????         ????????????????
       ?                        ? HTTP
       ?                        ?
????????????????                ?
?DecisionProc  ??????????????????
????????????????
       ?
       ?
????????????????
?DecisionHandler?
????????????????

        ???????????????????????
        ?FeatureCleanupService?
        ?                     ?
        ?• Deletes old sent   ?
        ?  features (7 days)  ?
        ?• Runs daily         ?
        ???????????????????????
```

---

## Key Architectural Changes

### 1. **Multi-Reader Signal Channel**

**Before:**
```csharp
SingleReader = true  // Only SignalWriterService
```

**After:**
```csharp
SingleReader = false // SignalWriterService + FeatureExtractorService
```

**Impact:**
- Both services now consume signals concurrently
- Collectors remain unchanged (they only write to the channel)
- Channel uses `FullMode.Wait` for backpressure

---

### 2. **New Components**

#### **FeatureExtractorService**
- **Type:** BackgroundService (runs continuously)
- **Input:** Signal channel reader
- **Processing:** 
  - Sliding time window aggregation
  - Feature extraction from signal events
  - Configurable window size and slide interval
- **Output:** FeatureRows ? SQLite Database

#### **FeatureStore (SQLite-based)**
- **Interface:** `IFeatureStore`
- **Implementation:** SQLite database at `spool/features.db`
- **Schema:**
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
- **Methods:**
  - `StoreAsync()`: Insert new feature row
  - `GetUnsentAsync()`: Get rows with `sent_flag = 0`
  - `MarkAsSentAsync()`: Update `sent_flag = 1` and `sent_at`
  - `GetByIdsAsync()`: Retrieve specific rows
  - `GetRangeAsync()`: Query by time range
  - `GetLatestAsync()`: Get most recent N rows
  - `DeleteOlderThanAsync()`: Clean up old sent rows

#### **FeatureUploadService**
- **Type:** BackgroundService
- **Frequency:** Every 2 minutes
- **Process:**
  1. Query unsent feature rows (limit 50)
  2. Convert to DTOs and send to backend (`POST /features`)
  3. On success: Mark rows as sent in database
  4. On failure: Retry with exponential backoff (5s ? 60s max)

#### **FeatureCleanupService**
- **Type:** BackgroundService
- **Frequency:** Daily
- **Process:**
  - Delete feature rows where `sent_flag = 1` and `window_start_ts < (now - 7 days)`
  - Prevents database bloat

#### **FeatureRow Contract**
- **Fields:**
  - `id`: Auto-increment primary key
  - `device_id`: Device identifier
  - `window_sec`: Window duration in seconds
  - `window_start_ts`: Window start timestamp (ISO 8601)
  - `feature_version`: Feature schema version (currently "1.0")
  - `features`: JSON dictionary of extracted features
  - `sent_flag`: 0 = unsent, 1 = sent
  - `sent_at`: Timestamp when marked as sent

#### **FeatureBatchRequest Contract**
- Batch upload request containing:
  - `device_id`: Device identifier
  - `features`: List of `FeatureRowDto` (simplified version without DB fields)
- **Processing:** 
  - Sliding time window aggregation
  - Feature extraction from signal events
  - Configurable window size and slide interval
- **Output:** FeatureRows ? FeatureStore

#### **FeatureStore**
- **Interface:** `IFeatureStore`
- **Implementation:** File-based JSONL storage (`spool/features.jsonl`)
- **Methods:**
  - `StoreAsync()`: Append feature row
  - `GetRangeAsync()`: Query by time range
  - `GetLatestAsync()`: Get most recent N rows

#### **FeatureRow Contract**
- **Fields:**
  - `WindowStart`, `WindowEnd`: Time window boundaries
  - `DeviceId`: Device identifier
  - `Features`: Dictionary of extracted features

---

## Configuration

### appsettings.json

```json
{
  "Agent": {
    "OutgoingQueueCapacity": 1000,
    "DecisionQueueCapacity": 100,
    "DefaultReportSeconds": 60,
    "StatusPollSeconds": 30
  },
  "FeatureExtractor": {
    "Enabled": true,
    "WindowSizeSeconds": 60,       // 60-second windows
    "WindowSlideSeconds": 30,       // Extract features every 30s
    "MaxEventsPerWindow": 1000      // Buffer limit
  }
}
```

---

## Service Startup Order

1. **EnrollOnStartupService** - Ensures device enrollment
2. **SignalWriterService** - Starts consuming signal channel
3. **FeatureExtractorService** - Starts consuming signal channel (parallel)
4. **Collectors** - Start producing signals (Session, App, Network)
5. **BatchProducerService** - Reads from spool file
6. **BatchSendService** - Sends batches to backend
7. **StatusPollService** - Polls backend for decisions
8. **DecisionProcessorService** - Processes decisions

---

## Feature Extraction Pipeline

### Input: Signal Events
```csharp
(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)
```

### Processing: Time-Windowed Aggregation
- Buffer events within sliding window (e.g., 60 seconds)
- Slide window periodically (e.g., every 30 seconds)
- Extract features from events in current window

### Output: Feature Row
```json
{
  "window_start": "2024-01-15T10:00:00Z",
  "window_end": "2024-01-15T10:01:00Z",
  "device_id": "device-123",
  "features": {
    "event_count": 42,
    "unique_event_types": 5,
    "count_SessionLock": 1,
    "count_ForegroundAppChanged": 12,
    "avg_interval_seconds": 1.43,
    "app_switch_count": 12,
    "unique_apps": 4,
    "network_change_count": 0
  }
}
```

---

## Extension Points

### 1. Custom Feature Extraction
Override `ExtractFeatures()` in `FeatureExtractorService.cs` to add:
- Behavioral biometrics
- Temporal patterns
- Anomaly scores
- Context embeddings

### 2. Custom Storage Backend
Implement `IFeatureStore` to use:
- SQL Database
- Time-series DB (InfluxDB, TimescaleDB)
- Cloud storage (Azure Blob, S3)
- In-memory cache (Redis)

### 3. Real-time ML Inference
Add a new service that:
- Reads from FeatureStore
- Feeds features to ML model
- Produces risk scores/predictions
- Writes to DecisionQueue for action

---

## Performance Characteristics

### Memory Usage
- **Signal Channel Buffer:** 1000 events max
- **Feature Window Buffer:** `MaxEventsPerWindow` (default: 1000)
- **Estimated per event:** ~500 bytes ? ~1MB total buffer

### Disk I/O
- **signals.jsonl:** Appends on every signal (frequent)
- **features.jsonl:** Appends every `WindowSlideSeconds` (periodic)

### CPU Usage
- **Feature extraction:** O(N) per window, where N = events in window
- **Window slide:** Triggered every `WindowSlideSeconds`

---

## Monitoring & Diagnostics

### Log Messages to Watch

? **Success:**
```
[FeatureExtractorService] Started for device {DeviceId}
[FeatureExtractorService] Stored feature row with 15 features for window ...
```

?? **Warnings:**
```
[FeatureExtractorService] Window buffer exceeded max size, dropping oldest event
[FeatureExtractorService] No events in window, skipping feature extraction
```

? **Errors:**
```
[FeatureExtractorService] Failed to extract and store features
[FeatureStore] Failed to store feature row
```

### Files to Monitor
- `spool/signals.jsonl` - Raw signal persistence
- `spool/features.jsonl` - Extracted features
- `spool/signals.offset` - Spool file read position
- `spool/enrollment.json` - Device enrollment state

---

## Benefits of This Architecture

1. **Separation of Concerns:** Raw signals and features stored separately
2. **Parallel Processing:** No bottleneck between persistence and extraction
3. **Flexibility:** Can enable/disable feature extraction independently
4. **Extensibility:** Easy to add new feature extraction logic
5. **Durability:** Both raw signals and features are persisted to disk
6. **Testability:** Components are loosely coupled via interfaces

---

## Next Steps

1. **Define ML Use Case:** What will the features be used for?
   - Anomaly detection?
   - Risk scoring?
   - User behavior profiling?

2. **Enhance Features:** Add domain-specific feature engineering
   - Keystroke dynamics
   - Mouse movement patterns
   - Application usage patterns

3. **Integrate ML Model:** Create inference pipeline
   - Load trained model
   - Consume features from store
   - Generate predictions

4. **Add Feature API:** Expose features via REST endpoints
   - Query features by time range
   - Export for offline training
   - Real-time feature serving

5. **Optimize Storage:** Implement feature archival/compression
   - Rolling file rotation
   - Compression of old features
   - Database migration for long-term storage
