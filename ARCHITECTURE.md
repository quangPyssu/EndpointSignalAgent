# Endpoint Signal Agent - Architecture Documentation

## Overview

The Endpoint Signal Agent is a background service that enrolls with a backend, collects signal events, sends batches to the backend, and polls for status decisions. The architecture follows a pipeline pattern with three main operational flows:

1. **Enrollment Flow** - Device registration and identity establishment
2. **Send Flow** - Signal collection, batching, and transmission
3. **Status Flow** - Periodic status polling and decision processing

---

## System Architecture

### Core Components

```
┌─────────────────────────────────────────────────────────────────┐
│                        Host Application                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌──────────────────┐         ┌──────────────────┐              │
│  │ EnrollmentStore  │────────▶│ BackendClient    │              │
│  │ (Singleton)      │         │ (HTTP Client)    │              │
│  └────────┬─────────┘         └──────────────────┘              │
│           │                                                       │
│           │ Provides Device ID                                   │
│           │                                                       │
│  ┌────────▼─────────┐         ┌──────────────────┐              │
│  │ BatchProducer    │────────▶│ Outgoing Channel │              │
│  │ Service          │         │ (Queue A)        │              │
│  └──────────────────┘         └────────┬─────────┘              │
│                                         │                         │
│                                         ▼                         │
│                               ┌──────────────────┐               │
│                               │ BatchSendService │               │
│                               │                  │               │
│                               └──────────────────┘               │
│                                                                   │
│  ┌──────────────────┐         ┌──────────────────┐              │
│  │ StatusPoll       │────────▶│ Decision Channel │              │
│  │ Service          │         │ (Queue B)        │              │
│  └──────────────────┘         └────────┬─────────┘              │
│                                         │                         │
│                                         ▼                         │
│                               ┌──────────────────┐               │
│                               │ DecisionProc     │               │
│                               │ Service          │               │
│                               └──────────────────┘               │
│                                                                   │
└─────────────────────────────────────────────────────────────────┘
```

### Communication Channels

The system uses two bounded channels for inter-service communication:

- **Queue A (Outgoing)**: `Channel<SignalBatchRequest>`
  - Capacity: Configurable (10-100,000, default from config)
  - Single writer: `BatchProducerService`
  - Single reader: `BatchSendService`
  - Full mode: `DropOldest`

- **Queue B (Decision)**: `Channel<StatusResponse>`
  - Capacity: Configurable (10-100,000, default from config)
  - Single writer: `StatusPollService`
  - Single reader: `DecisionProcessorService`
  - Full mode: `DropOldest`

---

## Flow 1: Enrollment

### Purpose
Establish device identity with the backend. All other services wait for enrollment completion before operating.

### Components

#### EnrollmentStore (Singleton)
**Location**: [src/Identity/IEnrollmentStore.cs](src/Identity/IEnrollmentStore.cs)

**Responsibility**: 
- Manage device enrollment lifecycle
- Persist and restore device ID
- Provide blocking API for services to wait on enrollment

**Key Methods**:
- `Start(CancellationToken)` - Initiates enrollment process
- `GetIdAsync(CancellationToken)` - Blocks until device ID is available

#### EnrollOnStartupService (Hosted Service)
**Location**: [src/Identity/IEnrollmentStore.cs](src/Identity/IEnrollmentStore.cs#L160)

**Responsibility**: Trigger enrollment on application startup

### Enrollment Flow Diagram

```
Application Startup
       │
       ▼
┌──────────────────────┐
│EnrollOnStartupService│
│  ExecuteAsync()      │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│EnrollmentStore.Start()│
└──────┬───────────────┘
       │
       ▼
┌──────────────────────────────┐
│Check enrollment.json exists? │
└──────┬─────────────────┬─────┘
       │                 │
    YES│              NO │
       ▼                 ▼
┌─────────────┐   ┌───────────────────┐
│Load from    │   │POST /api/enroll   │
│file         │   │{name: hostname}   │
└─────┬───────┘   └────────┬──────────┘
      │                    │
      │                    │ Retry loop with
      │                    │ exponential backoff
      │                    │ 5s → 7.5s → 11.25s
      │                    │ → ... → 60s (max)
      │                    │
      │                    ▼
      │            ┌───────────────────┐
      │            │Receive DeviceId   │
      │            │Save to file       │
      │            └────────┬──────────┘
      │                     │
      └─────────┬───────────┘
                ▼
        ┌───────────────────┐
        │ Set TCS Result    │
        │ (deviceId)        │
        └────────┬──────────┘
                 │
                 ▼
        ┌────────────────────┐
        │All GetIdAsync()    │
        │awaits complete     │
        └────────────────────┘
```

### Enrollment Details

#### 1. Startup Trigger
```csharp
// Program.cs registration
builder.Services.AddHostedService<EnrollOnStartupService>();

// Service immediately calls Start()
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.LogInformation("Starting enrollment service");
    _store.Start(stoppingToken);
    return Task.CompletedTask;
}
```

#### 2. Check Existing Enrollment
- **File Path**: `spool/enrollment.json`
- **Format**: 
  ```json
  {
    "DeviceId": "device-uuid-string",
    "EnrolledAt": "2026-01-20T10:30:00Z"
  }
  ```
- If valid file exists, deviceId is loaded immediately
- If file missing/invalid, proceed to enrollment

#### 3. Backend Enrollment
- **Endpoint**: `POST {BaseUrl}/api/enroll`
- **Request**: 
  ```json
  {
    "name": "MACHINE-NAME"
  }
  ```
- **Response**:
  ```json
  {
    "id": "device-uuid-string"
  }
  ```

#### 4. Retry Strategy
- **Initial Delay**: 5 seconds
- **Backoff Multiplier**: 1.5x
- **Max Delay**: 60 seconds
- **Retry Conditions**:
  - Network errors (socket exceptions)
  - HTTP errors (non-2xx status)
  - Malformed responses
- **No Retry Limit**: Continues until success or cancellation

#### 5. TaskCompletionSource Pattern
```csharp
private readonly TaskCompletionSource<string> _tcs = 
    new(TaskCreationOptions.RunContinuationsAsynchronously);

// Other services await this
public Task<string> GetIdAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);

// Set once enrollment completes
_tcs.TrySetResult(deviceId);
```

This allows all dependent services to block on `await enrollment.GetIdAsync()` until enrollment succeeds.

---

## Flow 2: Signal Send

### Purpose
Collect signals from various providers, batch them, and reliably transmit to backend with retry logic.

### Components

#### BatchProducerService (Hosted Service)
**Location**: [src/Services/BatchProducerService.cs](src/Services/BatchProducerService.cs)

**Responsibility**:
- Wait for enrollment completion
- Periodically collect signals from all registered providers
- Serialize signal batches to JSON
- Write batches to outgoing channel

#### BatchSendService (Hosted Service)
**Location**: [src/Services/BatchSendService.cs](src/Services/BatchSendService.cs)

**Responsibility**:
- Wait for enrollment completion
- Read batches from outgoing channel
- Transmit to backend with exponential backoff retry
- Handle transient failures gracefully

#### Signal Providers
**Location**: [src/Providers/](src/Providers/)

**Implementations**:
- `HeartbeatSignalProvider` - Periodic keepalive signals
- `SpoolFileSignalProvider` - Reads signals from disk spool

**Interface**:
```csharp
public interface ISignalProvider
{
    Task<IReadOnlyCollection<SignalEvent>> CollectAsync(CancellationToken ct);
}
```

### Send Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    BatchProducerService                          │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │await enrollment      │
                   │  .GetIdAsync()       │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Periodic Timer Loop   │
                   │(DefaultReportSeconds)│
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Collect from all      │
                   │SignalProviders       │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Serialize events      │
                   │to JSON string        │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Create                │
                   │SignalBatchRequest    │
                   │{id, data}            │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Write to              │
                   │Outgoing Channel      │
                   └──────────┬───────────┘
                              │
                              │
┌─────────────────────────────┼───────────────────────────────────┐
│                    BatchSendService                              │
└─────────────────────────────┼───────────────────────────────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │await enrollment      │
                   │  .GetIdAsync()       │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Read from             │
                   │Outgoing Channel      │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │POST /api/send        │
                   │{id, data}            │
                   └──────┬───────────────┘
                          │
                          │ Retry Loop
                          │
           ┌──────────────┼──────────────┐
           │              │              │
        SUCCESS        FAILURE      EXCEPTION
           │              │              │
           ▼              ▼              ▼
    ┌──────────┐  ┌────────────┐ ┌────────────┐
    │Reset     │  │Exponential │ │Exponential │
    │backoff   │  │backoff     │ │backoff     │
    │to 1s     │  │& retry     │ │& retry     │
    └────┬─────┘  └────┬───────┘ └────┬───────┘
         │             │              │
         │             │              │
         └─────────────┴──────────────┘
                       │
                       ▼
              ┌─────────────────┐
              │Next batch from  │
              │channel          │
              └─────────────────┘
```

### Send Flow Details

#### 1. Producer: Signal Collection
```csharp
// Waits until enrolled
var deviceId = await enrollment.GetIdAsync(stoppingToken);

// Periodic loop
while (!stoppingToken.IsCancellationRequested)
{
    // Collect from all providers
    var events = new List<SignalEvent>(capacity: 8);
    foreach (var provider in signalProviders)
    {
        var batch = await provider.CollectAsync(stoppingToken);
        if (batch.Count > 0) 
            events.AddRange(batch);
    }
    
    // Serialize to JSON
    var dataJson = JsonSerializer.Serialize(events);
    
    // Create batch request
    var req = new SignalBatchRequest(
        DeviceId: deviceId,
        Data: dataJson
    );
    
    // Write to channel (non-blocking)
    await outgoingQueue.Writer.WriteAsync(req, stoppingToken);
    
    // Wait for next interval
    var interval = state.GetReportSecondsOrDefault(
        agentOptions.Value.DefaultReportSeconds
    );
    await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
}
```

#### 2. Consumer: Batch Transmission
```csharp
// Waits until enrolled
var id = await enrollment.GetIdAsync(stoppingToken);

// Retry configuration
var backoff = TimeSpan.FromSeconds(1);
var backoffMax = TimeSpan.FromSeconds(30);

// Process channel
await foreach (var req in outgoingQueue.Reader.ReadAllAsync(stoppingToken))
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            var response = await backend.SendAsync(req, stoppingToken);
            
            if (response?.Success == true)
            {
                logger.LogDebug("Signal batch sent successfully");
                backoff = TimeSpan.FromSeconds(1); // Reset
                break; // Move to next batch
            }
            else
            {
                logger.LogWarning("Signal batch send returned success=false");
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(
                    Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds)
                );
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Send failed; retrying in {delay}s", 
                backoff.TotalSeconds);
            await Task.Delay(backoff, stoppingToken);
            backoff = TimeSpan.FromSeconds(
                Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds)
            );
        }
    }
}
```

#### 3. Backend Send API
- **Endpoint**: `POST {BaseUrl}/api/send`
- **Request**:
  ```json
  {
    "id": "device-uuid",
    "data": "[{\"type\":\"heartbeat\",\"timestamp\":\"2026-01-20T10:30:00Z\"}]"
  }
  ```
- **Response**:
  ```json
  {
    "success": true
  }
  ```

#### 4. Retry Strategy (Send)
- **Initial Backoff**: 1 second
- **Backoff Multiplier**: 2x
- **Max Backoff**: 30 seconds
- **Retry on**:
  - Any exception (network, HTTP, etc.)
  - `Success: false` in response
- **No Retry Limit**: Keeps retrying same batch until success

#### 5. Channel Overflow Behavior
- When channel is full: **Drops oldest batch**
- This prevents memory exhaustion under backpressure
- Newer data takes priority over old data

---

## Flow 3: Status Polling & Decision Processing

### Purpose
Periodically check backend for device status and process decisions (allow/deny/challenge).

### Components

#### StatusPollService (Hosted Service)
**Location**: [src/Services/StatusPollService.cs](src/Services/StatusPollService.cs)

**Responsibility**:
- Wait for enrollment completion
- Periodically poll backend for device status
- Write status responses to decision channel

#### DecisionProcessorService (Hosted Service)
**Location**: [src/Services/DecisionProcessorService.cs](src/Services/DecisionProcessorService.cs)

**Responsibility**:
- Read status responses from decision channel
- Delegate to decision handler for processing

#### IDecisionHandler
**Location**: [src/Handlers/](src/Handlers/)

**Implementations**:
- `DefaultDecisionHandler` - Logs decisions (stub implementation)

**Interface**:
```csharp
public interface IDecisionHandler
{
    void Handle(StatusResponse status);
}
```

### Status Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                      StatusPollService                           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │await enrollment      │
                   │  .GetIdAsync()       │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Periodic Timer Loop   │
                   │(StatusPollSeconds)   │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │POST /api/status      │
                   │{id: deviceId}        │
                   └──────┬───────────────┘
                          │
           ┌──────────────┼──────────────┐
           │              │              │
        SUCCESS       NON-NULL         ERROR
           │              │              │
           ▼              ▼              ▼
    ┌──────────┐  ┌────────────┐ ┌────────────┐
    │Got       │  │Got         │ │Log warning │
    │Status    │  │Status      │ │Continue    │
    │Response  │  │Response    │ └────┬───────┘
    └────┬─────┘  └────┬───────┘      │
         │             │               │
         └──────┬──────┘               │
                ▼                      │
    ┌──────────────────────┐          │
    │Write to              │          │
    │Decision Channel      │          │
    └──────────┬───────────┘          │
               │                      │
               └──────────┬───────────┘
                          │
                          ▼
                   ┌─────────────┐
                   │Wait interval│
                   │then repeat  │
                   └─────────────┘


┌─────────────────────────────────────────────────────────────────┐
│                  DecisionProcessorService                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Read from             │
                   │Decision Channel      │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │handler.Handle(status)│
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Process based on      │
                   │status value:         │
                   │ - "allow"            │
                   │ - "deny"             │
                   │ - "challenge"        │
                   │ - etc.               │
                   └──────────┬───────────┘
                              │
                              ▼
                   ┌──────────────────────┐
                   │Back to channel read  │
                   └──────────────────────┘
```

### Status Flow Details

#### 1. Periodic Status Polling
```csharp
// Wait for enrollment
var deviceId = await enrollment.GetIdAsync(stoppingToken);

// Poll loop
while (!stoppingToken.IsCancellationRequested)
{
    try
    {
        // Request status
        var req = new StatusRequest(DeviceId: deviceId);
        var status = await backend.PollStatusAsync(req, stoppingToken);
        
        // Write to channel if received
        if (status is not null)
            await decisionQueue.Writer.WriteAsync(status, stoppingToken);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Status poll failed");
        // Continue anyway - next poll will retry
    }
    
    // Wait for next poll interval
    await Task.Delay(
        TimeSpan.FromSeconds(agentOptions.Value.StatusPollSeconds), 
        stoppingToken
    );
}
```

#### 2. Backend Status API
- **Endpoint**: `POST {BaseUrl}/api/status`
- **Request**:
  ```json
  {
    "id": "device-uuid"
  }
  ```
- **Response**:
  ```json
  {
    "status": "allow"
  }
  ```

**Possible Status Values**:
- `"allow"` - Device is allowed to operate normally
- `"deny"` - Device should restrict operations
- `"challenge"` - User authentication required
- Custom backend-defined values

#### 3. Decision Processing
```csharp
// Simple consumer loop
await foreach (var status in decisionQueue.Reader.ReadAllAsync(stoppingToken))
{
    handler.Handle(status);
}
```

#### 4. Decision Handler Implementation
```csharp
// Default implementation (stub)
public class DefaultDecisionHandler : IDecisionHandler
{
    public void Handle(StatusResponse status)
    {
        _logger.LogInformation("Received status: {Status}", status.Status);
        
        // Future implementations might:
        // - Update agent state
        // - Trigger UI notifications
        // - Lock/unlock system features
        // - Initiate security responses
    }
}
```

#### 5. Error Handling
- **Poll Failures**: Logged but don't stop polling
- **No Retry Logic**: Waits for next scheduled poll
- **Channel Full**: Drops oldest status (newer decisions take priority)

---

## Synchronization & Dependencies

### Service Startup Order
1. **EnrollOnStartupService** starts first
2. **BatchProducerService** waits on `GetIdAsync()`
3. **BatchSendService** waits on `GetIdAsync()`
4. **StatusPollService** waits on `GetIdAsync()`
5. **DecisionProcessorService** starts immediately (no enrollment dependency)

### Blocking Points
All services that need device ID call:
```csharp
var deviceId = await enrollment.GetIdAsync(stoppingToken);
```

This blocks until:
- Enrollment file is loaded, OR
- Backend enrollment succeeds

### Graceful Shutdown
```csharp
// All services respond to cancellation token
while (!stoppingToken.IsCancellationRequested)
{
    // ... work ...
}

// Channels are completed on shutdown
outgoingQueue.Writer.TryComplete();
decisionQueue.Writer.TryComplete();
```

---

## Configuration

### Backend Options
**Section**: `Backend`

```json
{
  "Backend": {
    "BaseUrl": "http://localhost:8080",
    "TimeoutSeconds": 30,
    "EnrollPath": "/api/enroll",
    "SendPath": "/api/send",
    "StatusPath": "/api/status"
  }
}
```

### Agent Options
**Section**: `Agent`

```json
{
  "Agent": {
    "OutgoingQueueCapacity": 1000,
    "DecisionQueueCapacity": 100,
    "DefaultReportSeconds": 60,
    "StatusPollSeconds": 30
  }
}
```

**Validation**:
- `OutgoingQueueCapacity`: 10-100,000
- `DecisionQueueCapacity`: 10-100,000
- `DefaultReportSeconds`: 1-3600
- `StatusPollSeconds`: 1-3600

---

## Error Handling & Resilience

### Enrollment Resilience
- ✅ Persists to disk (`spool/enrollment.json`)
- ✅ Survives restarts (loads from disk)
- ✅ Infinite retry with exponential backoff
- ✅ Network failure tolerance

### Send Resilience
- ✅ Per-batch retry with exponential backoff
- ✅ Channel buffering (drops old if full)
- ✅ Network failure tolerance
- ✅ Service restart continues from next batch

### Status Resilience
- ✅ Individual poll failures logged, not fatal
- ✅ Next poll attempts automatically
- ✅ Channel buffering (drops old if full)
- ⚠️ No persistence (decisions lost on restart)

### General Patterns
1. **Catch-and-Continue**: Most errors logged but don't crash service
2. **Exponential Backoff**: Prevents thundering herd on backend issues
3. **Cancellation Tokens**: All async operations respect shutdown
4. **TaskCompletionSource**: Elegant async coordination primitive

---

## Data Flow Summary

### On Startup
```
1. Host starts all BackgroundServices in parallel
2. EnrollOnStartupService triggers EnrollmentStore.Start()
3. EnrollmentStore checks file → network → sets TCS
4. Other services unblock from GetIdAsync() await
5. All services begin normal operation
```

### Steady State (Normal Operation)
```
┌─────────────┐
│  Providers  │
└──────┬──────┘
       │ Signals
       ▼
┌──────────────┐     ┌───────────┐     ┌──────────┐
│BatchProducer │────▶│ Channel A │────▶│BatchSend │────▶ Backend
└──────────────┘     └───────────┘     └──────────┘
                                                │
                                                │ HTTP
                                                │
┌──────────────┐     ┌───────────┐     ┌───────▼──┐
│DecisionProc  │◀────│ Channel B │◀────│StatusPoll│◀──── Backend
└──────┬───────┘     └───────────┘     └──────────┘
       │
       ▼
┌──────────────┐
│DecisionHandle│
└──────────────┘
```

### Data Persistence
- **Enrollment**: Persisted to `spool/enrollment.json`
- **Signals**: Collected from `spool/signals.jsonl` (SpoolFileSignalProvider)
- **Offset**: Tracked in `spool/signals.offset`
- **Status Decisions**: In-memory only (not persisted)

---

## API Contracts

### Enrollment
```csharp
// Request
public sealed record EnrollRequest(
    [property: JsonPropertyName("name")] string DeviceName
);

// Response
public sealed record EnrollResponse(
    [property: JsonPropertyName("id")] string DeviceId
);
```

### Signal Batch
```csharp
// Request
public sealed record SignalBatchRequest(
    [property: JsonPropertyName("id")] string DeviceId,
    [property: JsonPropertyName("data")] string Data  // JSON array of SignalEvents
);

// Response
public sealed record SignalBatchResponse(
    [property: JsonPropertyName("success")] bool Success
);
```

### Status
```csharp
// Request
public sealed record StatusRequest(
    [property: JsonPropertyName("id")] string DeviceId
);

// Response
public sealed record StatusResponse(
    [property: JsonPropertyName("status")] string Status  // "allow", "deny", etc.
);
```

---

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
