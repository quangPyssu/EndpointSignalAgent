# Endpoint Signal Agent - Architecture Documentation

## Overview

The agent is a Windows Worker Service built on .NET 8. Runtime behavior is organized around four concurrent flows:

1. **Enrollment**: obtain/persist device identity.
2. **Signal ingestion**: collectors emit events into a broadcast pipeline.
3. **Signal send**: spool reader batches and sends events to backend.
4. **Status + decisions**: poll backend and process status decisions.

Feature extraction runs in parallel as a side consumer of the same live signal stream, but canonical research export is now raw collector JSONL (`spool/raw_signals.jsonl`).

---

## Runtime Topology (current)

```
Collectors (Application/Session/Network/SystemResource)
        │
        ▼
  SignalCollectorBase.WriteSignalAsync()
        │
        ▼
     ISignalBroadcaster
      ├──────────────► Channel #1 (SignalWriter) ─► SignalWriterService ─► spool/signals.jsonl (compat) + spool/raw_signals.jsonl (canonical)
      └──────────────► Channel #2 (FeatureExtractor) ─► FeatureExtractorService (live)

spool/signals.jsonl ─► SpoolFileSignalProvider ─► BatchProducerService ─► Channel<SignalBatchRequest> ─► BatchSendService ─► Backend
spool/raw_signals.jsonl ─► Offline replay extraction (window profiles: W60_S30, W120_S60, W30_S15)

Backend ─► StatusPollService ─► Channel<StatusResponse> ─► DecisionProcessorService ─► IDecisionHandler
```

---

## Dependency Registration (`src/Program.cs`)

### Channels

1. **Broadcast channels (fixed size 1000)**
   - Payload: `(SignalEventType Type, Dictionary<string,string> Payload, string SpoolPath)`
   - Full mode: `Wait`
   - Multi-writer, single-reader.
   - One channel for `SignalWriterService`, one for `FeatureExtractorService`.

2. **Outgoing send queue**
   - `Channel<SignalBatchRequest>`
   - Capacity from `Agent:OutgoingQueueCapacity`
   - Full mode: `DropOldest`
   - Single-writer (`BatchProducerService`), single-reader (`BatchSendService`).

3. **Decision queue**
   - `Channel<StatusResponse>`
   - Capacity from `Agent:DecisionQueueCapacity`
   - Full mode: `DropOldest`
   - Single-writer (`StatusPollService`), single-reader (`DecisionProcessorService`).

### Hosted Services

- `EnrollOnStartupService`
- `SignalWriterService`
- `SessionStateCollector`
- `ApplicationUsageCollector`
- `NetworkContextCollector`
- `SystemResourceCollector`
- `BatchProducerService`
- `BatchSendService`
- `FeatureExtractorService` (registered as singleton + hosted)
- `FeatureUploadService`
- `FeatureCleanupService`
- `KeyboardCommandService`
- `StatusPollService`
- `DecisionProcessorService`

---

## Flow 1: Enrollment

**Code**: `src/Bootstrap/Identity/EnrollmentStore.cs`

- `EnrollOnStartupService` calls `EnrollmentStore.Start(ct)` once.
- Store first tries loading `spool/enrollment.json`.
- If missing/invalid, it retries backend enrollment with exponential backoff:
  - start 5s, multiplier 1.5x, max 60s.
- Successful enrollment is persisted to `spool/enrollment.json`.
- `GetIdAsync(ct)` awaits an internal `TaskCompletionSource<string>` used by dependent services.

`Backend:UseBackend=false` is supported: backend operations are simulated in `BackendClient`.

---

## Flow 2: Signal Ingestion + Send

### Ingestion side

- Collectors call `WriteSignalAsync(...)` on `SignalCollectorBase`.
- Base class forwards to `ISignalBroadcaster` (no direct file write in collectors).
- Broadcaster writes every signal to both broadcast channels.

### Disk persistence side

- `SignalWriterService` reads the writer channel and dual-writes:
  - legacy send-compatible `spool/signals.jsonl` (old schema),
  - canonical replay-friendly `spool/raw_signals.jsonl` (`raw-collector-v1` envelope with signal provenance).

### Send side

- `SpoolFileSignalProvider` reads `spool/signals.jsonl` using byte-offset tracking in `spool/signals.offset`.
- `BatchProducerService` collects provider output, serializes events, and enqueues `SignalBatchRequest`.
- `BatchSendService` sends to backend with retry backoff (1s → 30s max, exponential x2).

---

## Flow 3: Status Poll + Decision Processing

**Code**:
- Poller: `src/Shared/Services/StatusPollService.cs`
- Processor: `src/Shared/Services/DecisionProcessorService.cs`

- Poller waits for enrollment, calls backend status endpoint on interval, pushes non-null responses to decision queue.
- Processor consumes queue and calls `IDecisionHandler.Handle(status)`.

---

## Flow 4: Feature Extraction

**Code**: `src/FeatureExtraction/Services/FeatureExtractorService.cs`

- Reads from dedicated feature channel (same signals as writer via broadcast).
- Uses sliding windows:
  - `WindowSizeSeconds`
  - `WindowSlideSeconds`
  - bounded by `MaxEventsPerWindow`.
- Extracts features via app/session/network aggregators and stores `FeatureRow` entries.
- Live extraction is gated by:
  - `FeatureExtractor:Enabled`
  - `FeatureExtractor:EnableLiveExtraction`.

On-demand extraction from a JSONL file is also supported via `ExtractFeaturesFromFileAsync(...)`.

---

## Configuration (validated on startup)

### Backend (`BackendOptions`)
- `UseBackend`
- `BaseUrl` (required absolute URL when `UseBackend=true`)
- `TimeoutSeconds`
- `EnrollPath`, `SendPath`, `StatusPath`

### Agent (`AgentOptions`)
- `OutgoingQueueCapacity` (10..100000)
- `DecisionQueueCapacity` (10..100000)
- `DefaultReportSeconds` (1..3600)
- `StatusPollSeconds` (1..3600)

### Feature Extractor (`FeatureExtractorOptions`)
- `Enabled`
- `EnableLiveExtraction`
- `WindowSizeSeconds` (10..3600)
- `WindowSlideSeconds` (5..3600)
- `MaxEventsPerWindow` (100..100000)

---

## Persistence Summary

- Enrollment: `spool/enrollment.json`
- Signals: `spool/signals.jsonl`
- Signal read offset: `spool/signals.offset`
- Decisions: in-memory only (not persisted)

## Future Enhancements

### Potential Improvements
1. **Status Persistence**: Save decisions to disk for post-restart replay
2. **Circuit Breaker**: Stop attempting after N consecutive failures
3. **Metrics/Telemetry**: Track send rates, failure rates, latencies
4. **Health Checks**: Expose health endpoints for monitoring
5. **Batch Compression**: Compress signal data before transmission
6. **Priority Queuing**: Critical signals take precedence
7. **Offline Queue**: Persist unsent batches to disk during outages
8. **Dynamic Intervals**: Backend can adjust report/poll frequencies

---

## Troubleshooting

### Symptom: Agent won't start
- **Check**: Backend connectivity (`BaseUrl` configuration)
- **Check**: Enrollment endpoint reachable
- **Logs**: Look for enrollment retry messages

### Symptom: Signals not sending
- **Check**: Channel capacity not exhausted
- **Check**: Backend `/api/send` endpoint health
- **Logs**: Look for exponential backoff warnings in BatchSendService

### Symptom: Status not updating
- **Check**: Backend `/api/status` endpoint responding
- **Check**: `StatusPollSeconds` configuration
- **Logs**: Look for poll warnings in StatusPollService

### Symptom: Memory growth
- **Check**: Channel capacities reasonable
- **Check**: Signal providers not leaking
- **Consider**: Reducing queue capacities

---

## References

- **Program.cs**: [src/Program.cs](src/Program.cs)
- **Enrollment**: [src/Identity/IEnrollmentStore.cs](src/Identity/IEnrollmentStore.cs)
- **Batch Producer**: [src/Services/BatchProducerService.cs](src/Services/BatchProducerService.cs)
- **Batch Sender**: [src/Services/BatchSendService.cs](src/Services/BatchSendService.cs)
- **Status Poll**: [src/Services/StatusPollService.cs](src/Services/StatusPollService.cs)
- **Decision Processor**: [src/Services/DecisionProcessorService.cs](src/Services/DecisionProcessorService.cs)
- **Backend Client**: [src/Clients/BackendClient.cs](src/Clients/BackendClient.cs)
- **Contracts**: [src/Contracts/](src/Contracts/)
