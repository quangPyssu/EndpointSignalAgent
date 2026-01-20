# Collectors - Detailed Technical Documentation

## Overview

Collectors are **background services** that continuously monitor system activity and write telemetry events to a local spool file. They run independently from the main signal sending pipeline and provide the raw data that gets transmitted to the backend.

### Architecture Pattern

```
┌─────────────────────────────────────────────────────────────┐
│                    Collector Services                        │
│  (Independent BackgroundServices, always running)            │
└─────────────────────────────────────────────────────────────┘
                          │
                          │ Write events
                          ▼
                ┌──────────────────────┐
                │ spool/signals.jsonl  │ ◄─── Append-only JSONL file
                │ (Disk buffer)        │
                └──────────────────────┘
                          │
                          │ Read by provider
                          ▼
                ┌──────────────────────┐
                │SpoolFileSignalProvider│
                └──────────┬───────────┘
                           │
                           ▼
                  ┌────────────────────┐
                  │BatchProducerService│
                  │                    │
                  └────────┬───────────┘
                           │
                           ▼
                     To Backend
```

### Key Design Principles

1. **Decoupled from Network**: Collectors write to disk regardless of network status
2. **Fire-and-Forget**: No backpressure - always collecting
3. **Append-Only**: JSONL format allows concurrent writes and simple recovery
4. **Privacy-Aware**: Hashes sensitive data (app paths) instead of storing plaintext
5. **Crash-Resistant**: If a collector crashes, others continue; data persists on disk

---

## Collector 1: ApplicationUsageCollector

**Location**: [src/Collectors/ApplicationUsageCollector.cs](src/Collectors/ApplicationUsageCollector.cs)

### Purpose
Monitors which application has foreground focus, tracks how long users spend in each app, and detects app-switching patterns to build a behavioral profile.

### What It Collects

| Event Type | Description | Data Captured |
|------------|-------------|---------------|
| `ForegroundChanged` | When user switches to a different app | App hash, category |
| `AppDwell` | How long user stayed in an app before switching | App hash, category, duration (ms) |
| `AppSwitchRate` | Number of app switches in a time window | Window size, switch count |

### How It Works

#### 1. Polling Loop (750ms interval)
```csharp
private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(750);

while (!stoppingToken.IsCancellationRequested)
{
    var fg = TryGetForegroundApp();  // Get current foreground window
    
    // Compare with previous
    if (!fg.Equals(_current))
    {
        // App changed! Emit dwell event for old app
        // Emit ForegroundChanged for new app
    }
    
    await Task.Delay(_pollInterval, stoppingToken);
}
```

**Why 750ms?**
- Fast enough to catch brief app switches
- Slow enough to not waste CPU
- Balances accuracy vs. overhead

#### 2. Foreground App Detection (Windows-specific)
```csharp
private static ForegroundApp? TryGetForegroundApp()
{
    // Get window handle of foreground window
    var hwnd = GetForegroundWindow();
    
    // Get process ID from window handle
    GetWindowThreadProcessId(hwnd, out uint pid);
    
    // Get process details
    using var p = Process.GetProcessById((int)pid);
    
    // Get exe name and full path
    var exeName = p.ProcessName;
    var fullPath = p.MainModule?.FileName;  // Can fail for protected processes
    
    // Hash the path for privacy
    var appKey = HashStable(fullPath ?? exeName);
    
    // Categorize the app
    var category = Categorize(exeName);
    
    return new ForegroundApp(appKey, category);
}
```

**Win32 APIs Used**:
- `GetForegroundWindow()` - Returns handle to foreground window
- `GetWindowThreadProcessId()` - Maps window to process ID

#### 3. Privacy: SHA-256 Hashing
```csharp
private static string HashStable(string input)
{
    using var sha = SHA256.Create();
    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes.AsSpan(0, 12)); // 24 hex chars
}
```

**Why hash?**
- Original path: `C:\Users\John\AppData\Local\Chrome\chrome.exe`
- Hashed: `5F3A7B8C9D2E1F4A6B7C8D9E`
- Backend can track patterns without knowing exact apps
- Consistent hash = same app always has same identifier
- Can't reverse-engineer to get app names

#### 4. App Categorization
```csharp
private static string Categorize(string exeName)
{
    exeName = exeName.ToLowerInvariant();
    
    // Browser
    if (exeName is "chrome" or "msedge" or "firefox" or "brave") 
        return "Browser";
    
    // IDE / Dev
    if (exeName is "devenv" or "rider64" or "code") 
        return "IDE";
    
    // Communication
    if (exeName is "slack" or "teams" or "discord") 
        return "Comms";
    
    // Office
    if (exeName is "winword" or "excel" or "powerpnt") 
        return "Office";
    
    return "Other";
}
```

**Purpose**: Group apps for behavioral analysis without knowing specific apps.

#### 5. Dwell Time Tracking
```csharp
// State tracking
private ForegroundApp? _current;              // Currently focused app
private DateTimeOffset _currentSince;         // When it became focused

// When app changes
if (!fg.Equals(_current))
{
    // Calculate how long user was in previous app
    var dwellMs = (long)(now - _currentSince).TotalMilliseconds;
    
    // Emit dwell event
    await WriteEventAsync(new {
        ts = now,
        type = "AppDwell",
        payload = new {
            appKey = _current!.Value.AppKey,
            category = _current!.Value.Category,
            durationMs = dwellMs
        }
    }, stoppingToken);
    
    // Update state to new app
    _current = fg;
    _currentSince = now;
}
```

#### 6. Switch Rate Tracking (60-second windows)
```csharp
private readonly TimeSpan _switchRateWindow = TimeSpan.FromSeconds(60);
private int _switchesInWindow = 0;
private DateTimeOffset _windowStart;

// Every 60 seconds
if (now - _windowStart >= _switchRateWindow)
{
    await WriteEventAsync(new {
        ts = now,
        type = "AppSwitchRate",
        payload = new {
            windowSec = 60,
            switches = _switchesInWindow  // e.g., 15 switches in 60 seconds
        }
    }, stoppingToken);
    
    _switchesInWindow = 0;  // Reset counter
    _windowStart = now;     // Start new window
}

// Increment on each switch
if (!fg.Equals(_current))
{
    _switchesInWindow++;
}
```

**Why track switch rate?**
- High switch rate = multitasking, distraction, or context switching
- Low switch rate = focused work
- Can indicate stress, productivity patterns, or multitasking behavior

### Example Events Written to Spool

```json
{"ts":"2026-01-20T10:30:00.123Z","type":"ForegroundChanged","payload":{"appKey":"5F3A7B8C9D2E1F4A","category":"Browser"}}
{"ts":"2026-01-20T10:32:15.456Z","type":"AppDwell","payload":{"appKey":"5F3A7B8C9D2E1F4A","category":"Browser","durationMs":135333}}
{"ts":"2026-01-20T10:32:15.456Z","type":"ForegroundChanged","payload":{"appKey":"8C9D2E1F4A6B7C8D","category":"IDE"}}
{"ts":"2026-01-20T10:31:00.000Z","type":"AppSwitchRate","payload":{"windowSec":60,"switches":8}}
```

### Edge Cases Handled

1. **No Foreground Window**: Lock screen, UAC prompt, secure desktop
   - Returns `null`, skips that poll cycle
   
2. **Protected Processes**: System apps, elevated processes
   - Falls back to process name if full path unavailable
   
3. **Process Access Denied**: Some processes can't be inspected
   - Catches exception, returns `null`

4. **Shutdown Flush**: On service stop
   ```csharp
   // Emit final dwell event for current app
   await WriteEventAsync(new {
       // ... dwell data ...
       reason = "shutdown_flush"
   }, CancellationToken.None);
   ```

---

## Collector 2: SessionStateCollector

**Location**: [src/Collectors/SessionStateCollector.cs](src/Collectors/SessionStateCollector.cs)

### Purpose
Monitors user session state (lock/unlock) and idle time to detect when user is actively using the machine vs. away from keyboard.

### What It Collects

| Event Type | Description | Data Captured |
|------------|-------------|---------------|
| `SessionLock` | User locked the screen (Win+L) | Timestamp, reason |
| `SessionUnlock` | User unlocked the screen | Timestamp, reason |
| `IdleSample` | Periodic sample of idle time | Idle milliseconds, bucketed value |

### How It Works

#### 1. Dual-Mode Operation
This collector runs **two concurrent monitoring mechanisms**:

```csharp
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Mechanism 1: Event-driven session watching
    StartSessionWatcher();
    
    // Mechanism 2: Polling-based idle monitoring
    return IdleLoop(stoppingToken);
}
```

---

### Mechanism 1: Session Lock/Unlock Events (Event-Driven)

#### Windows System Events
```csharp
private void StartSessionWatcher()
{
    // Subscribe to Windows session change events
    Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
}

private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
{
    var type = e.Reason switch
    {
        SessionSwitchReason.SessionLock => SignalEventType.SessionLock,
        SessionSwitchReason.SessionUnlock => SignalEventType.SessionUnlock,
        _ => SignalEventType.Unknown
    };
    
    if (type != SignalEventType.Unknown)
    {
        await WriteAsync(type, new Dictionary<string, string> {
            ["reason"] = e.Reason.ToString()
        });
    }
}
```

**How Windows Notifies**:
- `SystemEvents.SessionSwitch` is a .NET wrapper around Windows session notifications
- Fires when:
  - User presses Win+L (lock)
  - User unlocks with password
  - Fast user switching
  - Remote desktop connect/disconnect

**Why Event-Driven?**
- Lock/unlock are infrequent events
- No point polling - let OS notify us
- Zero CPU overhead between events

---

### Mechanism 2: Idle Time Monitoring (Polling-Based)

#### The Idle Loop (2-second polling)
```csharp
private async Task IdleLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // Get milliseconds since last keyboard/mouse input
        var idleMs = GetIdleMilliseconds();
        var idleSec = (int)(idleMs / 1000);
        
        // Bucket to reduce spam (0, 5, 10, 15, 20, ...)
        var bucketSec = (idleSec / 5) * 5;
        
        // Only emit when bucket changes
        if (bucketSec != _lastIdleBucketSec)
        {
            _lastIdleBucketSec = bucketSec;
            
            await WriteAsync(SignalEventType.IdleSample, 
                new Dictionary<string, string> {
                    ["idleMs"] = idleMs.ToString(),
                    ["idleBucketSec"] = bucketSec.ToString()
                });
        }
        
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
}
```

**Bucketing Strategy**:
```
Raw Idle Time    Bucket    Emit Event?
─────────────    ──────    ───────────
0-4999ms    →    0 sec  →  Yes (if changed)
5000-9999ms →    5 sec  →  Yes (if changed)
10000ms     →   10 sec  →  Yes (if changed)
10500ms     →   10 sec  →  No (same bucket)
15000ms     →   15 sec  →  Yes (bucket changed)
```

**Why bucket?**
- Without: Emit event every 2 seconds with slightly different values (noisy!)
- With: Only emit when crossing 5-second thresholds (clean signal)
- Reduces spool file size by ~60%

#### Win32 Idle Detection
```csharp
[StructLayout(LayoutKind.Sequential)]
private struct LASTINPUTINFO
{
    public uint cbSize;
    public uint dwTime;  // Tick count of last input event
}

[DllImport("user32.dll")]
private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

private static ulong GetIdleMilliseconds()
{
    var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
    GetLastInputInfo(ref lii);
    
    // Get current tick count
    var now = unchecked((uint)Environment.TickCount);
    
    // Calculate idle time (handles wrap-around with unsigned math)
    var idleMs = unchecked(now - lii.dwTime);
    
    return idleMs;
}
```

**How It Works**:
1. Windows tracks last keyboard/mouse input in system tick count
2. `GetLastInputInfo()` returns that tick count
3. Subtract from current tick count = idle duration
4. Uses `unchecked` arithmetic to handle 32-bit tick wrap-around (every ~49 days)

**What Counts as "Input"?**
- Keyboard press
- Mouse movement
- Mouse click
- Touch input
- Does **NOT** include:
  - Video playback
  - Background processes
  - Network activity

### Thread Safety: Write Lock
```csharp
private readonly SemaphoreSlim _writeLock = new(1, 1);

private async Task WriteAsync(SignalEventType type, Dictionary<string, string> payload)
{
    await _writeLock.WaitAsync();
    try
    {
        using var writer = new SpoolFileCollector(_spoolPath);
        await writer.WriteAsync(new SignalEvent(DateTimeOffset.UtcNow, type, payload));
    }
    finally
    {
        _writeLock.Release();
    }
}
```

**Why needed?**
- `OnSessionSwitch` fires on arbitrary Windows thread (event callback)
- `IdleLoop` runs on background service thread
- Both write to same file
- Semaphore prevents concurrent writes from corrupting file

### Example Events Written to Spool

```json
{"ts":"2026-01-20T10:30:00.000Z","type":"SessionLock","payload":{"reason":"SessionLock"}}
{"ts":"2026-01-20T10:30:05.123Z","type":"IdleSample","payload":{"idleMs":"5123","idleBucketSec":"5"}}
{"ts":"2026-01-20T10:30:11.456Z","type":"IdleSample","payload":{"idleMs":"11456","idleBucketSec":"10"}}
{"ts":"2026-01-20T10:35:20.789Z","type":"SessionUnlock","payload":{"reason":"SessionUnlock"}}
{"ts":"2026-01-20T10:35:22.000Z","type":"IdleSample","payload":{"idleMs":"234","idleBucketSec":"0"}}
```

### Use Cases

**Idle Time**:
- Detect breaks, meetings, lunch
- Distinguish active work from presence
- Inactivity patterns (e.g., always idle 12:00-13:00 = lunch)

**Lock/Unlock**:
- Security compliance (do users lock screens?)
- Session boundaries (work start/end times)
- Multi-user device detection

### Cleanup on Shutdown
```csharp
public override Task StopAsync(CancellationToken cancellationToken)
{
    // Unsubscribe from Windows events to prevent memory leaks
    Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
    
    return base.StopAsync(cancellationToken);
}
```

---

## Collector 3: SpoolFileCollector (Utility)

**Location**: [src/Collectors/SpoolFileCollector.cs](src/Collectors/SpoolFileCollector.cs)

### Purpose
**Not a BackgroundService** - this is a **utility class** that provides thread-safe, append-only writing to the JSONL spool file. Used by other collectors and the provider.

### Key Features

#### 1. Thread-Safe Writing
```csharp
private readonly SemaphoreSlim _writeLock = new(1, 1);

public async Task WriteAsync(SignalEvent signalEvent, CancellationToken ct = default)
{
    await _writeLock.WaitAsync(ct);
    try
    {
        // Write to file
    }
    finally
    {
        _writeLock.Release();
    }
}
```

**Prevents**:
- Interleaved writes from multiple threads
- Corrupted JSON lines
- Partial writes

#### 2. Append-Only File Mode
```csharp
using var fs = new FileStream(
    _spoolPath,
    FileMode.Append,        // Always append to end
    FileAccess.Write,       // Write-only
    FileShare.Read,         // Allow others to read
    bufferSize: 4096,
    useAsync: true          // Async I/O
);
```

**Why Append-Only?**
- Simple crash recovery (no overwrites)
- Multiple writers can append concurrently (with locking)
- Readers can read while writers append
- No data loss on crash (everything on disk stays)

#### 3. JSONL Format (JSON Lines)
```csharp
foreach (var ev in signalEvents)
{
    var lineObj = new {
        ts = ev.TimestampUtc,
        type = ev.Type.ToString(),
        payload = ev.Payload
    };
    
    var json = JsonSerializer.Serialize(lineObj, _jsonOptions);
    var lineBytes = Encoding.UTF8.GetBytes(json + "\n");  // Add newline
    
    await fs.WriteAsync(lineBytes, ct);
}

await fs.FlushAsync(ct);  // Ensure written to disk
```

**JSONL Format**:
- One JSON object per line
- Each line is complete, valid JSON
- Lines are independent (can read/process one at a time)
- Perfect for append-only logs

**Example File**:
```jsonl
{"ts":"2026-01-20T10:30:00Z","type":"SessionLock","payload":{"reason":"SessionLock"}}
{"ts":"2026-01-20T10:30:05Z","type":"IdleSample","payload":{"idleMs":"5000","idleBucketSec":"5"}}
{"ts":"2026-01-20T10:30:10Z","type":"ForegroundChanged","payload":{"appKey":"ABC123","category":"Browser"}}
```

#### 4. JSON Serialization Options
```csharp
_jsonOptions = new JsonSerializerOptions
{
    Converters = { new JsonStringEnumConverter() },  // Enums as strings
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase  // camelCase keys
};
```

**Result**:
- `Type` enum serialized as `"SessionLock"` not `1`
- Property names: `type`, `payload`, `ts` (not `Type`, `Payload`, `TimestampUtc`)

### Usage by Other Collectors

```csharp
// In ApplicationUsageCollector
await File.AppendAllTextAsync(_spoolPath, line + Environment.NewLine, ct);

// In SessionStateCollector
using var writer = new SpoolFileCollector(_spoolPath);
await writer.WriteAsync(new SignalEvent(...));
```

**Note**: `ApplicationUsageCollector` writes raw JSON, bypassing `SpoolFileCollector`. This is less clean but functional.

---

## Data Flow: Collectors → Provider → Backend

### Step 1: Collectors Write to Spool
```
┌──────────────────────┐
│ApplicationUsage      │──┐
│Collector             │  │
└──────────────────────┘  │
                          │ Append events
┌──────────────────────┐  │
│SessionState          │──┼─▶ spool/signals.jsonl
│Collector             │  │
└──────────────────────┘  │
                          │
┌──────────────────────┐  │
│(Future collectors)   │──┘
└──────────────────────┘
```

### Step 2: Provider Reads from Spool
```csharp
// SpoolFileSignalProvider (configured in Program.cs)
builder.Services.AddSingleton<ISignalProvider>(sp =>
{
    return new SpoolFileSignalProvider(
        spoolPath: @"spool\signals.jsonl",
        offsetPath: @"spool\signals.offset",  // Track reading position
        logger: logger
    );
});
```

### Step 3: BatchProducer Collects from Provider
```csharp
// In BatchProducerService
while (!stoppingToken.IsCancellationRequested)
{
    var events = new List<SignalEvent>();
    
    foreach (var provider in signalProviders)
    {
        var batch = await provider.CollectAsync(stoppingToken);
        if (batch.Count > 0) 
            events.AddRange(batch);
    }
    
    // Send to backend...
}
```

### Step 4: Provider Tracks Offset
```
spool/signals.jsonl:
Line 1: {"ts":"...","type":"SessionLock",...}
Line 2: {"ts":"...","type":"IdleSample",...}
Line 3: {"ts":"...","type":"AppDwell",...}
        ^
        └─── Offset file stores: "3" (next line to read)
```

**Crash Recovery**:
- If agent crashes after sending lines 1-3
- On restart, offset file says "start at line 4"
- Lines 1-3 already sent, won't be re-sent
- No data loss, no duplicates

---

## Configuration & Tuning

### ApplicationUsageCollector Tuning Knobs
```csharp
private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(750);
private readonly TimeSpan _switchRateWindow = TimeSpan.FromSeconds(60);
```

**Trade-offs**:
- **Lower _pollInterval** (e.g., 500ms):
  - ✅ Catch brief app switches
  - ❌ Higher CPU usage
  
- **Higher _pollInterval** (e.g., 2000ms):
  - ✅ Lower CPU usage
  - ❌ Miss quick switches

- **Lower _switchRateWindow** (e.g., 30s):
  - ✅ More granular switch patterns
  - ❌ More events, larger spool

### SessionStateCollector Tuning
```csharp
// Idle check interval
await Task.Delay(TimeSpan.FromSeconds(2), ct);

// Bucketing granularity
var bucketSec = (idleSec / 5) * 5;  // 5-second buckets
```

**Trade-offs**:
- **Smaller buckets** (e.g., 1 second):
  - ✅ More precise idle tracking
  - ❌ Many more events
  
- **Larger buckets** (e.g., 15 seconds):
  - ✅ Fewer events
  - ❌ Less precise

---

## Error Handling & Resilience

### Collector-Level Error Handling
All collectors use this pattern:
```csharp
while (!stoppingToken.IsCancellationRequested)
{
    try
    {
        // Do collection work
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown - propagate
        throw;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Collection loop error");
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        // Continue loop - don't crash the service
    }
}
```

**Philosophy**: **Fail soft, keep collecting**
- One bad sample shouldn't crash the collector
- Network errors don't affect collectors (they write to disk)
- Disk full? Log warning, keep trying

### Write Failure Handling
```csharp
// In ApplicationUsageCollector
await File.AppendAllTextAsync(_spoolPath, line + Environment.NewLine, ct);
```

**Potential failures**:
- Disk full → Exception caught, logged, next poll tries again
- Permission denied → Logged on startup, crashes early (fail fast)
- File locked → OS handles retry (write queued)

---

## Platform Support

### Current: Windows-Only
All collectors use Windows-specific APIs:
- `user32.dll` - GetForegroundWindow, GetLastInputInfo
- `Microsoft.Win32.SystemEvents` - Session notifications
- `Process.MainModule` - Windows process info

### Future: Cross-Platform
To support Linux/macOS:
```csharp
#if WINDOWS
    // Windows implementation
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
#elif LINUX
    // X11 or Wayland implementation
#elif MACOS
    // NSWorkspace implementation
#endif
```

---

## Privacy & Security Considerations

### Data Privacy
1. **Application Paths Hashed**: SHA-256 hash prevents path leakage
2. **No Window Titles**: Collector doesn't capture window text
3. **No Keystroke Content**: Only timing, not what was typed
4. **Local-First**: All data written to disk first, backend connection not required

### Security Implications
1. **Process Enumeration**: Collector can see all process names (requires user privileges)
2. **Idle Detection**: Can infer when user is away (privacy concern for monitoring)
3. **Screen Lock Detection**: Reveals user habits (could be sensitive)

### Recommendations
- Deploy with user consent
- Disclose what data is collected
- Provide opt-out mechanisms
- Secure backend transmission (HTTPS)

---

## Troubleshooting

### Symptom: No events in spool file
**Check**:
1. Directory created? `spool/` folder exists?
2. Write permissions? Run as admin if needed
3. Collector services running? Check logs for startup messages

### Symptom: Only some event types appear
**Check**:
1. Windows-specific collectors won't work on Linux/Mac
2. Protected processes might cause collection failures
3. Check logs for exceptions

### Symptom: Spool file grows too large
**Solutions**:
1. Increase send frequency (reduce `DefaultReportSeconds`)
2. Adjust collector intervals (reduce collection frequency)
3. Implement log rotation (truncate after successful send)

### Symptom: High CPU usage
**Check**:
1. `ApplicationUsageCollector._pollInterval` too low?
2. Multiple collectors fighting for file lock?
3. Antivirus scanning spool file on every write?

---

## Future Enhancements

### Potential New Collectors
1. **NetworkStateCollector**
   - WiFi SSID (hashed)
   - VPN connection status
   - Network adapter changes
   
2. **ProcessLifecycleCollector**
   - Process start/stop events
   - Crash detection
   - Elevated process launches

3. **PerformanceCollector**
   - CPU usage per app
   - Memory pressure
   - Battery level

4. **ClipboardCollector** (privacy-sensitive!)
   - Clipboard activity (hashed content)
   - Copy/paste frequency

### Improvements to Existing Collectors
1. **Adaptive Polling**: Slow down when idle, speed up when active
2. **Event Batching**: Write multiple events in one I/O operation
3. **Compression**: Compress spool file periodically
4. **Circular Buffer**: Limit spool file size, drop oldest events

---

## Code References

- **ApplicationUsageCollector**: [src/Collectors/ApplicationUsageCollector.cs](src/Collectors/ApplicationUsageCollector.cs)
- **SessionStateCollector**: [src/Collectors/SessionStateCollector.cs](src/Collectors/SessionStateCollector.cs)
- **SpoolFileCollector**: [src/Collectors/SpoolFileCollector.cs](src/Collectors/SpoolFileCollector.cs)
- **SignalEvent**: [src/Contracts/SignalEvent.cs](src/Contracts/SignalEvent.cs)
- **Registration**: [src/Program.cs](src/Program.cs#L72-L74) (collectors as hosted services)

---

## Summary

### Collectors Design Philosophy

| Aspect | Approach | Rationale |
|--------|----------|-----------|
| **Coupling** | Loosely coupled from network | Network outages don't stop collection |
| **Storage** | Disk-buffered (JSONL) | Survives crashes, enables async sending |
| **Privacy** | Hash sensitive data | Behavioral patterns without PII |
| **Errors** | Fail soft, keep running | One bad sample doesn't crash service |
| **Threading** | Independent services | Collectors don't block each other |
| **Format** | Structured JSON | Easy to parse, extend, debug |

### Key Takeaways

1. **Collectors write, Providers read** - Clear separation of concerns
2. **Spool file is the buffer** - Decouples data collection from transmission
3. **Polling vs. Events** - Use events when possible, poll when necessary
4. **Privacy by design** - Hash, don't store raw sensitive data
5. **Resilience first** - Keep collecting even when things fail
