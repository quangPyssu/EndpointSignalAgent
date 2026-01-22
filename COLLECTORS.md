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
4. **Privacy-Aware**: Hashes sensitive data (app paths, SSIDs, IPs) instead of storing plaintext
5. **Crash-Resistant**: If a collector crashes, others continue; data persists on disk

---

## Collector 1: ApplicationUsageCollector

**Location**: [src/Collectors/ApplicationUsageCollector.cs](src/Collectors/ApplicationUsageCollector.cs)

### Purpose
Monitors which application has foreground focus, tracks how long users spend in each app, and detects app-switching patterns to build a behavioral profile.

### What It Collects

| Event Type | Description | Data Captured |
|------------|-------------|---------------|
| `ForegroundAppChanged` | When user switches to a different app | App hash, category |
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
    if (hwnd == IntPtr.Zero) return null;
    
    // Get process ID from window handle
    GetWindowThreadProcessId(hwnd, out uint pid);
    if (pid == 0) return null;
    
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
    if (exeName is "devenv" or "rider64" or "code" or "idea64" or "clion64") 
        return "IDE";
    if (exeName is "cmd" or "powershell" or "pwsh" or "windowsTerminal" or "wt") 
        return "Terminal";
    
    // Communication
    if (exeName is "slack" or "teams" or "discord" or "zoom" or "skype") 
        return "Comms";
    
    // Office
    if (exeName is "winword" or "excel" or "powerpnt" or "onenote") 
        return "Office";
    
    // Media
    if (exeName is "spotify" or "vlc" or "musicbee") 
        return "Media";
    
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
    if (dwellMs < 0) dwellMs = 0;
    
    // Emit dwell event
    await WriteSignalAsync(SignalEventType.AppDwell, new Dictionary<string, string>
    {
        ["appKey"] = _current!.Value.AppKey,
        ["category"] = _current!.Value.Category,
        ["durationMs"] = dwellMs.ToString()
    });
    
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
    await WriteSignalAsync(SignalEventType.AppSwitchRate, new Dictionary<string, string>
    {
        ["windowSec"] = ((int)_switchRateWindow.TotalSeconds).ToString(),
        ["switches"] = _switchesInWindow.ToString()
    });
    
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
{"ts":"2026-01-20T10:30:00.123Z","type":"ForegroundAppChanged","payload":{"appKey":"5F3A7B8C9D2E1F4A","category":"Browser"}}
{"ts":"2026-01-20T10:32:15.456Z","type":"AppDwell","payload":{"appKey":"5F3A7B8C9D2E1F4A","category":"Browser","durationMs":"135333"}}
{"ts":"2026-01-20T10:32:15.456Z","type":"ForegroundAppChanged","payload":{"appKey":"8C9D2E1F4A6B7C8D","category":"IDE"}}
{"ts":"2026-01-20T10:31:00.000Z","type":"AppSwitchRate","payload":{"windowSec":"60","switches":"8"}}
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
   if (_current is not null)
   {
       var dwellMs = (long)(now - _currentSince).TotalMilliseconds;
       await WriteSignalAsync(SignalEventType.AppDwell, new Dictionary<string, string>
       {
           ["appKey"] = _current!.Value.AppKey,
           ["category"] = _current!.Value.Category,
           ["durationMs"] = dwellMs.ToString(),
           ["reason"] = "shutdown_flush"
       });
   }
   ```

---

## Collector 2: SessionStateCollector

**Location**: [src/Collectors/SessionStateCollector.cs](src/Collectors/SessionStateCollector.cs)

### Purpose
Monitors user session state (lock/unlock), idle time, screensaver activity, and display power state to detect when user is actively using the machine vs. away from keyboard.

### What It Collects

| Event Type | Description | Data Captured |
|------------|-------------|---------------|
| `SessionLock` | User locked the screen (Win+L) | Timestamp, reason |
| `SessionUnlock` | User unlocked the screen | Timestamp, reason |
| `IdleSample` | Periodic sample of idle time | Idle milliseconds, bucketed value |
| `ScreenSaverOn` | Screensaver activated | Running state, idle time |
| `ScreenSaverOff` | Screensaver deactivated | Running state, idle time |
| `DisplayOn` | Monitor turned on | Display state, source |
| `DisplayOff` | Monitor turned off | Display state, source |
| `DisplayDimmed` | Monitor dimmed | Display state, source |

### How It Works

#### 1. Multi-Mode Operation
This collector runs **three concurrent monitoring mechanisms**:

```csharp
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    Directory.CreateDirectory("spool");
    
    // Mechanism 1: Event-driven session watching
    StartSessionWatcher();
    
    // Mechanism 2: Event-driven display state watching
    StartDisplayStateWatcher();
    
    // Mechanism 3: Polling-based idle + screensaver monitoring
    return IdleAndScreenSaverLoop(stoppingToken);
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
        await WriteSignalAsync(type, new Dictionary<string, string> {
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

### Mechanism 2: Display Power State Monitoring (Event-Driven)

This is the most sophisticated collector mechanism, using a **hidden message-only window** to receive Windows power broadcast notifications.

#### Display State Listener Architecture
```csharp
private sealed class DisplayStateListener : IDisposable
{
    private Thread? _thread;                    // Background thread for message pump
    private IntPtr _hwnd = IntPtr.Zero;         // Hidden window handle
    private IntPtr _notifyHandle = IntPtr.Zero; // Power notification registration
    private WndProcDelegate? _wndProc;          // Window procedure callback
    
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
        new("6FE69556-704A-47A0-8F24-C28D936FDA47");
}
```

#### Step-by-Step: How Display State Detection Works

**Step 1: Create Background Thread with Message Pump**
```csharp
public void Start()
{
    _thread = new Thread(ThreadMain)
    {
        IsBackground = true,
        Name = "DisplayStateListener"
    };
    
    // Window message pump requires STA apartment
    _thread.SetApartmentState(ApartmentState.STA);
    
    _thread.Start();
    
    // Wait for window creation (with timeout)
    _ready.Wait(TimeSpan.FromSeconds(3));
}
```

**Why a separate thread?**
- Windows message pump requires a dedicated thread
- Can't block the collector's main loop
- STA apartment state required for COM interop

**Step 2: Register Message-Only Window**
```csharp
private void ThreadMain()
{
    var hInstance = GetModuleHandle(null);
    var className = "EndpointSignalAgent_DisplayListener_" + Guid.NewGuid().ToString("N");
    
    // Register window class
    var wc = new WNDCLASSEX
    {
        cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
        hInstance = hInstance,
        lpszClassName = className
    };
    
    RegisterClassEx(ref wc);
    
    // Create invisible message-only window
    _hwnd = CreateWindowEx(
        0, className, className, 0,
        0, 0, 0, 0,
        HWND_MESSAGE,  // Parent = message-only window
        IntPtr.Zero, hInstance, IntPtr.Zero);
}
```

**What is a message-only window?**
- Hidden window (no UI)
- Exists only to receive Windows messages
- Uses `HWND_MESSAGE` as parent (special handle = -3)
- Lightweight - no graphics overhead

**Step 3: Register for Power Notifications**
```csharp
// Register for console display state changes
var displayStateGuid = GUID_CONSOLE_DISPLAY_STATE;
_notifyHandle = RegisterPowerSettingNotification(
    _hwnd, 
    ref displayStateGuid, 
    DEVICE_NOTIFY_WINDOW_HANDLE);
```

**What is GUID_CONSOLE_DISPLAY_STATE?**
- Windows power setting GUID: `6FE69556-704A-47A0-8F24-C28D936FDA47`
- Notifies when monitor power state changes
- States: 0 = Off, 1 = On, 2 = Dimmed

**Step 4: Message Loop**
```csharp
// Standard Windows message loop
while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
{
    TranslateMessage(ref msg);
    DispatchMessage(ref msg);  // Calls WndProcImpl
}
```

**Step 5: Process Power Broadcasts**
```csharp
private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
{
    if ((int)msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE)
    {
        // Parse power setting structure from unmanaged memory
        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
        
        if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE && setting.DataLength >= 4)
        {
            // Read state data (4 bytes after structure header)
            var dataOffset = Marshal.SizeOf<POWERBROADCAST_SETTING>();
            var state = Marshal.ReadInt32(lParam, dataOffset);
            
            // Notify collector (async, don't block message pump)
            _onChange(new DisplayStateChange(state, "GUID_CONSOLE_DISPLAY_STATE"));
        }
        
        return new IntPtr(1);  // Handled
    }
    
    if ((int)msg == WM_CLOSE)
    {
        PostQuitMessage(0);
        return IntPtr.Zero;
    }
    
    return DefWindowProc(hWnd, msg, wParam, lParam);
}
```

**Thread-Safe Event Dispatch**
```csharp
private void OnDisplayStateChanged(DisplayStateChange change)
{
    // Called on message pump thread - don't block it!
    if (_lastDisplayState == change.State)
        return;  // Debounce duplicate events
    
    _lastDisplayState = change.State;
    
    var type = change.State switch
    {
        0 => SignalEventType.DisplayOff,
        1 => SignalEventType.DisplayOn,
        2 => SignalEventType.DisplayDimmed,
        _ => SignalEventType.Unknown
    };
    
    // Fire-and-forget async write (don't block message pump)
    _ = Task.Run(async () =>
    {
        await WriteSignalAsync(type, new Dictionary<string, string>
        {
            ["displayState"] = change.State switch
            {
                0 => "Off",
                1 => "On",
                2 => "Dimmed",
                _ => "Unknown"
            },
            ["source"] = change.Source
        });
    });
}
```

**Why use Task.Run?**
- `OnDisplayStateChanged` called on message pump thread
- Can't block with `await` (would freeze message processing)
- Fire-and-forget ensures message pump stays responsive

#### Display State Use Cases
- **DisplayOff**: Monitor went to sleep (power saving, user pressed power button)
- **DisplayOn**: User returned, monitor woke up
- **DisplayDimmed**: Transitioning to sleep (warning signal)

---

### Mechanism 3: Idle Time + Screensaver Monitoring (Polling-Based)

#### The Idle and Screensaver Loop (2-second polling)
```csharp
private async Task IdleAndScreenSaverLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // 1. Check idle time
        var idleMs = GetIdleMilliseconds();
        var idleSec = (int)(idleMs / 1000);
        
        // Bucket to reduce spam (0, 5, 10, 15, 20, ...)
        var bucketSec = (idleSec / 5) * 5;
        
        // Only emit when bucket changes
        if (bucketSec != _lastIdleBucketSec)
        {
            _lastIdleBucketSec = bucketSec;
            
            await WriteSignalAsync(SignalEventType.IdleSample, 
                new Dictionary<string, string> {
                    ["idleMs"] = idleMs.ToString(),
                    ["idleBucketSec"] = bucketSec.ToString()
                });
        }
        
        // 2. Check screensaver state
        var ssRunning = TryGetScreenSaverRunning();
        if (ssRunning.HasValue && ssRunning != _lastScreenSaverRunning)
        {
            _lastScreenSaverRunning = ssRunning;
            
            await WriteSignalAsync(
                ssRunning.Value ? SignalEventType.ScreenSaverOn : SignalEventType.ScreenSaverOff,
                new Dictionary<string, string>
                {
                    ["running"] = ssRunning.Value ? "true" : "false",
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

#### Screensaver Detection
```csharp
private const uint SPI_GETSCREENSAVERRUNNING = 114;

[DllImport("user32.dll", SetLastError = true)]
private static extern bool SystemParametersInfo(
    uint uiAction, 
    uint uiParam, 
    out bool pvParam, 
    uint fWinIni);

private static bool? TryGetScreenSaverRunning()
{
    if (SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, out var running, 0))
        return running;
    
    return null;
}
```

**How Screensaver Detection Works**:
- `SystemParametersInfo` is a Win32 API for system-wide settings
- `SPI_GETSCREENSAVERRUNNING` (114) queries screensaver state
- Returns: `true` = screensaver running, `false` = not running
- State transitions emit events (not every poll)

**Why track screensaver separately from idle?**
- User can be idle without screensaver (disabled, long timeout)
- Screensaver = visual indicator of extended absence
- Different security implications (some screensavers require password to dismiss)

### Thread Safety: Write Lock
```csharp
private readonly SemaphoreSlim _writeLock = new(1, 1);

protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
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
- `OnDisplayStateChanged` fires on message pump thread
- `IdleAndScreenSaverLoop` runs on background service thread
- All three write to same file
- Semaphore prevents concurrent writes from corrupting file

### Example Events Written to Spool

```json
{"ts":"2026-01-20T10:30:00.000Z","type":"SessionLock","payload":{"reason":"SessionLock"}}
{"ts":"2026-01-20T10:30:05.123Z","type":"IdleSample","payload":{"idleMs":"5123","idleBucketSec":"5"}}
{"ts":"2026-01-20T10:30:11.456Z","type":"IdleSample","payload":{"idleMs":"11456","idleBucketSec":"10"}}
{"ts":"2026-01-20T10:30:15.789Z","type":"ScreenSaverOn","payload":{"running":"true","idleMs":"15789","idleBucketSec":"15"}}
{"ts":"2026-01-20T10:30:20.000Z","type":"DisplayOff","payload":{"displayState":"Off","source":"GUID_CONSOLE_DISPLAY_STATE"}}
{"ts":"2026-01-20T10:35:18.000Z","type":"DisplayOn","payload":{"displayState":"On","source":"GUID_CONSOLE_DISPLAY_STATE"}}
{"ts":"2026-01-20T10:35:20.789Z","type":"SessionUnlock","payload":{"reason":"SessionUnlock"}}
{"ts":"2026-01-20T10:35:22.000Z","type":"ScreenSaverOff","payload":{"running":"false","idleMs":"234","idleBucketSec":"0"}}
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

**Screensaver**:
- Extended absence indicator
- Visual security indicator
- Correlate with idle time for accuracy

**Display State**:
- Power management patterns
- After-hours activity detection
- Monitor sleep = likely user absence

### Cleanup on Shutdown
```csharp
public override Task StopAsync(CancellationToken cancellationToken)
{
    try
    {
        // Unsubscribe from Windows events to prevent memory leaks
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
    catch { /* ignore */ }
    
    try
    {
        // Dispose display listener (stops message pump, destroys window)
        _displayListener?.Dispose();
        _displayListener = null;
    }
    catch { /* ignore */ }
    
    return base.StopAsync(cancellationToken);
}
```

---

## Collector 3: NetworkContextCollector

**Location**: [src/Collectors/NetworkContextCollector.cs](src/Collectors/NetworkContextCollector.cs)

### Purpose
Monitors network context changes including VPN connectivity, WiFi status, WiFi SSID (hashed), local network topology, and public IP address bucket (coarsened and hashed) to detect location and network environment changes while preserving privacy.

### What It Collects

| Event Type | Description | Data Captured |
|------------|-------------|---------------|
| `VpnStateChanged` | VPN connection established/dropped | VPN on/off, hashed adapter fingerprint |
| `WifiLinkChanged` | WiFi adapter went up/down | WiFi link state (boolean) |
| `WifiSsidChanged` | Connected to different WiFi network | Hashed SSID |
| `LocalNetworkChanged` | Local IP subnet changed | Hashed /24 IPv4 prefix |
| `PublicIpBucketChanged` | Public IP address bucket changed | Hashed IP bucket (/24 for IPv4, /48 for IPv6) |

### How It Works

#### 1. Multi-Frequency Polling Strategy
```csharp
private readonly TimeSpan _poll = TimeSpan.FromSeconds(3);           // Local network checks
private readonly TimeSpan _publicIpPoll = TimeSpan.FromSeconds(60);  // Public IP checks
private DateTimeOffset _nextPublicIpPoll = DateTimeOffset.MinValue;
```

**Why different poll intervals?**
- **Local network changes** (VPN, WiFi): Fast detection needed (3 seconds)
- **Public IP changes**: Slow-changing, expensive HTTP call (60 seconds)
- Balances responsiveness with external API rate limits

#### 2. Main Collection Loop
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var now = DateTimeOffset.UtcNow;
        
        // 1. Snapshot local network state (VPN, WiFi, local IP)
        var snap = SnapshotLocal();
        
        // 2. Check VPN state change
        if (_lastVpnOn != snap.VpnOn || _lastVpnAdapterHash != snap.VpnAdapterHash)
        {
            // Emit VpnStateChanged event
        }
        
        // 3. Check WiFi link state change
        if (_lastWifiUp != snap.WifiUp)
        {
            // Emit WifiLinkChanged event
        }
        
        // 4. Check WiFi SSID change
        var ssidHash = TryGetWifiSsidHash();
        if (_lastWifiSsidHash != ssidHash)
        {
            // Emit WifiSsidChanged event
        }
        
        // 5. Check local network prefix change
        if (_lastLocalPrefixHash != snap.LocalPrefixHash)
        {
            // Emit LocalNetworkChanged event
        }
        
        // 6. Periodically check public IP (slower poll)
        if (now >= _nextPublicIpPoll)
        {
            _nextPublicIpPoll = now + _publicIpPoll;
            var publicBucketHash = await TryGetPublicIpBucketHashAsync(stoppingToken);
            if (publicBucketHash is not null && publicBucketHash != _lastPublicIpBucketHash)
            {
                // Emit PublicIpBucketChanged event
            }
        }
        
        await Task.Delay(_poll, stoppingToken);
    }
}
```

---

### Feature 1: VPN Detection

#### Heuristic VPN Detection
```csharp
private static (bool VpnOn, string? VpnAdapterHash, bool WifiUp, string? LocalPrefixHash) SnapshotLocal()
{
    bool vpnOn = false;
    string? vpnAdapter = null;
    bool wifiUp = false;
    string? localPrefix = null;
    
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        
        var type = ni.NetworkInterfaceType;
        var desc = (ni.Description ?? "").ToLowerInvariant();
        var name = (ni.Name ?? "").ToLowerInvariant();
        
        // Heuristic VPN detection
        bool isVpn =
            type == NetworkInterfaceType.Tunnel ||
            desc.Contains("vpn") || desc.Contains("wireguard") || desc.Contains("openvpn") ||
            desc.Contains("tunnel") || desc.Contains("tap") || desc.Contains("tunsafe") ||
            name.Contains("vpn") || name.Contains("wireguard") || name.Contains("openvpn");
        
        if (isVpn)
        {
            vpnOn = true;
            vpnAdapter ??= HashStable($"vpn|{ni.Id}|{ni.Description}|{ni.Name}");
        }
        
        // ... (WiFi and local IP detection continues)
    }
    
    return (vpnOn, vpnAdapter, wifiUp, localPrefix);
}
```

**VPN Detection Strategy**:
1. **Primary indicator**: `NetworkInterfaceType.Tunnel` (most reliable)
2. **Keyword matching**: Description/name contains "vpn", "wireguard", "openvpn", "tunnel", "tap"
3. **Hashed fingerprint**: Combines adapter ID, description, and name for privacy-preserving identification

**Why heuristic?**
- No universal Windows API for "is this a VPN?"
- Different VPN clients use different adapter types
- Covers most common VPN solutions:
  - OpenVPN (TAP adapter)
  - WireGuard (Tunnel adapter)
  - Windows built-in VPN (Tunnel)
  - Cisco AnyConnect, NordVPN, etc.

**VPN Adapter Hash**:
```csharp
vpnAdapter = HashStable($"vpn|{ni.Id}|{ni.Description}|{ni.Name}");
```
- Unique identifier for VPN adapter
- Allows backend to track "same VPN" vs. "different VPN"
- Privacy-preserving: can't reverse to get VPN provider name

---

### Feature 2: WiFi SSID Detection (Native Windows API)

This is the most complex network detection feature, using **wlanapi.dll** to query WiFi state.

#### WiFi SSID Detection Flow
```csharp
private static string? TryGetWifiSsid()
{
    IntPtr client = IntPtr.Zero;
    IntPtr ifListPtr = IntPtr.Zero;
    IntPtr dataPtr = IntPtr.Zero;
    
    try
    {
        // Step 1: Open WLAN client handle
        var err = WlanOpenHandle(2, IntPtr.Zero, out _, out client);
        if (err != 0) return null;
        
        // Step 2: Enumerate WiFi interfaces
        err = WlanEnumInterfaces(client, IntPtr.Zero, out ifListPtr);
        if (err != 0 || ifListPtr == IntPtr.Zero) return null;
        
        // Step 3: Parse interface list from unmanaged memory
        var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(ifListPtr);
        long current = ifListPtr.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>();
        int infoSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
        
        // Step 4: Iterate through interfaces
        for (int i = 0; i < header.dwNumberOfItems; i++)
        {
            var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(new IntPtr(current));
            current += infoSize;
            
            // Step 5: Query current connection for this interface
            err = WlanQueryInterface(
                client,
                ref info.InterfaceGuid,
                WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                IntPtr.Zero,
                out uint dataSize,
                out dataPtr,
                out _);
            
            if (err != 0 || dataPtr == IntPtr.Zero) continue;
            
            try
            {
                // Step 6: Parse connection attributes
                var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                
                // Only process if connected
                if (conn.isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                    continue;
                
                // Step 7: Extract SSID from connection
                var ssidBytes = conn.wlanAssociationAttributes.dot11Ssid.SSID;
                var len = (int)conn.wlanAssociationAttributes.dot11Ssid.SSIDLength;
                
                if (len <= 0 || len > ssidBytes.Length) continue;
                
                // Step 8: Decode SSID (usually UTF-8, but can be arbitrary bytes)
                return Encoding.UTF8.GetString(ssidBytes, 0, len);
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) WlanFreeMemory(dataPtr);
                dataPtr = IntPtr.Zero;
            }
        }
        
        return null;
    }
    catch
    {
        return null;
    }
    finally
    {
        // Cleanup: Free all unmanaged memory
        if (dataPtr != IntPtr.Zero) WlanFreeMemory(dataPtr);
        if (ifListPtr != IntPtr.Zero) WlanFreeMemory(ifListPtr);
        if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
    }
}
```

**Why use wlanapi.dll instead of .NET APIs?**
- .NET `NetworkInterface` doesn't expose SSID
- Need native Windows WiFi API
- `wlanapi.dll` is the official Windows WiFi API

**WiFi State Machine**:
```
wlan_interface_state_not_ready = 0       // Hardware not ready
wlan_interface_state_connected = 1       // Connected to network ← We want this
wlan_interface_state_ad_hoc_network_formed = 2
wlan_interface_state_disconnecting = 3
wlan_interface_state_disconnected = 4    // Not connected
wlan_interface_state_associating = 5     // Connecting...
wlan_interface_state_discovering = 6     // Scanning for networks
wlan_interface_state_authenticating = 7  // Authenticating...
```

**SSID Hashing for Privacy**:
```csharp
private static string? TryGetWifiSsidHash()
{
    var ssid = TryGetWifiSsid();
    if (string.IsNullOrWhiteSpace(ssid)) return null;
    return HashStable($"ssid|{ssid.Trim()}");
}
```

**Example**:
- Raw SSID: `"CoffeeShop_Free_WiFi"`
- Hashed: `"A3F5B2D8E1C4F7A9B2D5"`
- Backend can track location patterns without knowing actual WiFi names

---

### Feature 3: Local Network Fingerprint

#### IPv4 Subnet Detection
```csharp
// Inside SnapshotLocal() loop
if (localPrefix is null)
{
    var ipProps = ni.GetIPProperties();
    var v4 = ipProps.UnicastAddresses
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    
    if (v4 is not null)
    {
        var bytes = v4.GetAddressBytes();
        var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        localPrefix = HashStable($"local4|{prefix}");
    }
}
```

**What is local network fingerprint?**
- Takes local IPv4 address (e.g., `192.168.1.45`)
- Coarsens to /24 subnet (e.g., `192.168.1.0/24`)
- Hashes the result

**Why /24 subnet?**
- Typical home/office network size
- Same subnet = same physical network
- Different subnet = different network (e.g., home vs. office)
- Coarse enough to preserve privacy

**Example**:
- Local IP: `192.168.1.45`
- Subnet: `192.168.1.0/24`
- Hashed: `"C7D2E4F6A8B1C3D5"`

**Use Cases**:
- Detect network changes without knowing exact IP
- Correlate with WiFi SSID for location fingerprinting
- Works even when WiFi disabled (e.g., Ethernet)

---

### Feature 4: Public IP Bucket Detection

#### Public IP Detection via HTTP API
```csharp
private static readonly HttpClient _http = new(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
})
{
    Timeout = TimeSpan.FromSeconds(2)
};

private static async Task<string?> TryGetPublicIpBucketHashAsync(CancellationToken ct)
{
    try
    {
        // Use ipify.org API (free, no rate limits, plain text response)
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org");
        req.Headers.UserAgent.ParseAdd("EndpointSignalAgent/1.0");
        
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        
        var ipText = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        if (string.IsNullOrWhiteSpace(ipText)) return null;
        
        if (!IPAddress.TryParse(ipText, out var ip)) return null;
        
        // Coarsen to bucket and hash
        return HashStable($"pub|{ComputeIpBucketString(ip)}");
    }
    catch
    {
        return null;  // Network error, API down, etc.
    }
}
```

**Why ipify.org?**
- Free, no API key required
- Simple plain-text response (not JSON)
- Supports both IPv4 and IPv6
- Fast (CDN-backed)
- No rate limits for reasonable use

**IP Bucketing Algorithm**:
```csharp
private static string ComputeIpBucketString(IPAddress ip)
{
    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        // IPv4: Use /24 bucket (e.g., 203.0.113.0/24)
        var b = ip.GetAddressBytes();
        return $"v4|{b[0]}.{b[1]}.{b[2]}.0/24";
    }
    
    // IPv6: Use /48 bucket (first 3 hextets)
    var bytes = ip.GetAddressBytes();
    ushort h0 = (ushort)((bytes[0] << 8) | bytes[1]);
    ushort h1 = (ushort)((bytes[2] << 8) | bytes[3]);
    ushort h2 = (ushort)((bytes[4] << 8) | bytes[5]);
    return $"v6|{h0:x4}:{h1:x4}:{h2:x4}::/48";
}
```

**IPv4 Bucketing Example**:
```
Full IP:     203.0.113.45
Bucket:      203.0.113.0/24
String:      "v4|203.0.113.0/24"
Hashed:      "D5F2A7C8E3B1F4A6"
```

**IPv6 Bucketing Example**:
```
Full IP:     2001:0db8:85a3:0000:0000:8a2e:0370:7334
Bucket:      2001:0db8:85a3::/48
String:      "v6|2001:0db8:85a3::/48"
Hashed:      "E7F3B8D2C5A1F9B4"
```

**Why bucket public IPs?**
- **Privacy**: Full IP can identify exact location/ISP
- **/24 for IPv4**: Typically represents a small ISP block (~256 addresses)
- **/48 for IPv6**: Standard ISP allocation to customers
- Granular enough to detect location changes
- Coarse enough to avoid precise tracking

**Why hash buckets?**
- Even bucketed IPs can reveal ISP/region
- Hashing with machine salt makes cross-device correlation harder
- Backend sees consistent identifier per location, not raw IPs

---

### Feature 5: Machine-Specific Salt

#### Per-Machine Hashing Salt
```csharp
private static readonly string? _machineSalt = TryGetMachineGuid();

private static string? TryGetMachineGuid()
{
    try
    {
        return Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", 
            "MachineGuid", 
            null) as string;
    }
    catch
    {
        return null;
    }
}

private static string HashStable(string input)
{
    // Salt with machine GUID (if available)
    var salted = _machineSalt is null ? input : $"{_machineSalt}|{input}";
    
    using var sha = SHA256.Create();
    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
    return Convert.ToHexString(bytes.AsSpan(0, 12)); // 24 hex chars
}
```

**What is Machine GUID?**
- Unique identifier per Windows installation
- Stored in registry: `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`
- Generated during Windows installation
- Persists across reboots (changes on Windows reinstall)

**Why use machine-specific salt?**
1. **Prevents rainbow table attacks**: Can't pre-compute hashes for common SSIDs
2. **Cross-device privacy**: Same WiFi network hashes differently on different devices
3. **Non-linkability**: Backend can't correlate networks across devices by hash alone

**Example**:
```
Device A, SSID "Starbucks":
- Salt: "12345678-ABCD-EFGH-IJKL-MNOPQRSTUVWX"
- Hash: "A3F5B2D8E1C4F7A9"

Device B, SSID "Starbucks":
- Salt: "87654321-ZYXW-VUSR-QPON-MLKJIHGFEDCB"
- Hash: "D8C3F2A9B5E7D1F4"

→ Different hashes, can't link devices via network
```

---

### Sentinel Values for Initial State

#### Baseline Event Strategy
```csharp
private string? _lastWifiSsidHash = "__init__";      // Sentinel: force first event
private string? _lastPublicIpBucketHash = "__init__"; // Sentinel: force first event
```

**Why sentinel values?**
- First poll: Compare against sentinel
- Mismatch triggers event (even if WiFi is disconnected)
- Provides baseline snapshot on startup
- Subsequent polls: Compare against real previous value

**Example Flow**:
```
Startup:
  _lastWifiSsidHash = "__init__"
  Current SSID hash = "A3F5B2D8" (or null if disconnected)
  Mismatch → Emit WifiSsidChanged with current state

Next poll:
  _lastWifiSsidHash = "A3F5B2D8"
  Current SSID hash = "A3F5B2D8"
  Match → No event (no change)
```

---

### Example Events Written to Spool

```json
{"ts":"2026-01-20T10:00:00.000Z","type":"VpnStateChanged","payload":{"vpnOn":"false","vpnAdapter":"none"}}
{"ts":"2026-01-20T10:00:00.001Z","type":"WifiLinkChanged","payload":{"wifiUp":"true"}}
{"ts":"2026-01-20T10:00:00.002Z","type":"WifiSsidChanged","payload":{"wifiSsid":"A3F5B2D8E1C4F7A9","wifiUp":"true"}}
{"ts":"2026-01-20T10:00:00.003Z","type":"LocalNetworkChanged","payload":{"localPrefix":"C7D2E4F6A8B1C3D5"}}
{"ts":"2026-01-20T10:00:01.000Z","type":"PublicIpBucketChanged","payload":{"publicIpBucket":"D5F2A7C8E3B1F4A6"}}
{"ts":"2026-01-20T10:05:30.000Z","type":"VpnStateChanged","payload":{"vpnOn":"true","vpnAdapter":"F8D3A7B5C2E1F4D6"}}
{"ts":"2026-01-20T10:05:33.000Z","type":"PublicIpBucketChanged","payload":{"publicIpBucket":"E9C4F2A8D5B7E3F1"}}
```

---

## Collector 4: SpoolFileCollector (Utility)

**Location**: [src/Collectors/SpoolFileCollector.cs](src/Collectors/SpoolFileCollector.cs)

### Purpose
**Not a BackgroundService** - this is a **utility class** that provides thread-safe, append-only writing to the JSONL spool file. Used by collectors via the `SignalCollectorBase` base class.

### Key Features

#### 1. Append-Only File Mode
```csharp
public async Task WriteAsync(IEnumerable<SignalEvent> signalEvents, CancellationToken ct = default)
{
    using var fs = new FileStream(
        _spoolPath,
        FileMode.Append,        // Always append to end
        FileAccess.Write,       // Write-only
        FileShare.Read,         // Allow others to read
        bufferSize: 4096,
        useAsync: true          // Async I/O
    );
    
    foreach (var ev in signalEvents)
    {
        var lineObj = new {
            ts = ev.TimestampUtc,
            type = ev.Type.ToString(),
            payload = ev.Payload
        };
        
        var json = JsonSerializer.Serialize(lineObj, _jsonOptions);
        var lineBytes = Encoding.UTF8.GetBytes(json + "\n");
        
        await fs.WriteAsync(lineBytes, ct);
    }
    
    await fs.FlushAsync(ct);  // Ensure written to disk
}
```

**Why Append-Only?**
- Simple crash recovery (no overwrites)
- Multiple writers can append concurrently (with locking)
- Readers can read while writers append
- No data loss on crash (everything on disk stays)

#### 2. JSONL Format (JSON Lines)
**JSONL Format**:
- One JSON object per line
- Each line is complete, valid JSON
- Lines are independent (can read/process one at a time)
- Perfect for append-only logs

**Example File**:
```jsonl
{"ts":"2026-01-20T10:30:00Z","type":"SessionLock","payload":{"reason":"SessionLock"}}
{"ts":"2026-01-20T10:30:05Z","type":"IdleSample","payload":{"idleMs":"5000","idleBucketSec":"5"}}
{"ts":"2026-01-20T10:30:10Z","type":"ForegroundAppChanged","payload":{"appKey":"ABC123","category":"Browser"}}
```

#### 3. JSON Serialization Options
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

#### 4. Directory Auto-Creation
```csharp
public SpoolFileCollector(string spoolPath)
{
    _spoolPath = spoolPath;
    
    var dir = Path.GetDirectoryName(_spoolPath);
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);  // Creates "spool" directory if needed
}
```
// In ApplicationUsageCollector
await File.AppendAllTextAsync(_spoolPath, line + Environment.NewLine, ct);

// In SessionStateCollector
using var writer = new SpoolFileCollector(_spoolPath);
await writer.WriteAsync(new SignalEvent(...));
```

**Note**: `ApplicationUsageCollector` writes raw JSON, bypassing `SpoolFileCollector`. This is less clean but functional.

---

## Collector Base Class: SignalCollectorBase

**Location**: [src/Collectors/SignalCollectorBase.cs](src/Collectors/SignalCollectorBase.cs)

### Purpose
Abstract base class for all collectors, providing common functionality:
- Inherits from `BackgroundService` (hosted service lifecycle)
- Thread-safe write mechanism
- Spool file path management

### Implementation
```csharp
public abstract class SignalCollectorBase : BackgroundService, IDisposable
{
    private readonly string _spoolPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    
    protected SignalCollectorBase(string spoolPath)
    {
        _spoolPath = spoolPath;
    }
    
    protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
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
    
    public override void Dispose()
    {
        _writeLock.Dispose();
        base.Dispose();
    }
}
```

**Benefits**:
- All collectors get thread-safe writes for free
- Consistent spool file path
- Single point of write logic
- Semaphore ensures no concurrent file corruption

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
│NetworkContext        │──┘
│Collector             │
└──────────────────────┘
```

### Step 2: Provider Reads from Spool
```csharp
// SpoolFileSignalProvider (configured in Program.cs)
builder.Services.AddSingleton<ISignalProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SpoolFileSignalProvider>>();
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
    
    // Serialize to JSON and send to backend...
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

### NetworkContextCollector Tuning
```csharp
private readonly TimeSpan _poll = TimeSpan.FromSeconds(3);           // Local network checks
private readonly TimeSpan _publicIpPoll = TimeSpan.FromSeconds(60);  // Public IP checks
```

**Trade-offs**:
- **Lower _poll** (e.g., 1s):
  - ✅ Faster network change detection
  - ❌ More CPU/network overhead
  
- **Lower _publicIpPoll** (e.g., 30s):
  - ✅ Faster public IP change detection
  - ❌ More external API calls, may hit rate limits

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
// In SignalCollectorBase
await _writeLock.WaitAsync();
try
{
    using var writer = new SpoolFileCollector(_spoolPath);
    await writer.WriteAsync(new SignalEvent(...));
}
finally
{
    _writeLock.Release();
}
```

**Potential failures**:
- Disk full → Exception caught, logged, next poll tries again
- Permission denied → Logged on startup, crashes early (fail fast)
- File locked → OS handles retry (write queued)

---

## Platform Support

### Current: Windows-Only
All collectors use Windows-specific APIs:
- **ApplicationUsageCollector**: `user32.dll` - GetForegroundWindow
- **SessionStateCollector**: 
  - `user32.dll` - GetLastInputInfo, SystemParametersInfo
  - `Microsoft.Win32.SystemEvents` - Session notifications
  - Win32 message pump for display state
- **NetworkContextCollector**: 
  - `wlanapi.dll` - WiFi SSID detection
  - `Microsoft.Win32.Registry` - Machine GUID

### Future: Cross-Platform
To support Linux/macOS:
```csharp
#if WINDOWS
    // Windows implementation
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
#elif LINUX
    // X11 or Wayland implementation
    // xdotool, wmctrl, or direct X11 API
#elif MACOS
    // NSWorkspace implementation
    // NSWorkspace.sharedWorkspace.activeApplication
#endif
```

**Cross-Platform Challenges**:
- **Foreground window**: Different APIs per OS/window manager
- **WiFi SSID**: Linux = NetworkManager/wpa_supplicant, macOS = CoreWLAN
- **Session lock**: Linux = varied (depends on DE), macOS = ScreenSaver framework
- **Display state**: Linux = DPMS, macOS = IOKit

---

## Privacy & Security Considerations

### Data Privacy
1. **Application Paths Hashed**: SHA-256 hash prevents path leakage
2. **WiFi SSIDs Hashed**: Can't reverse to get network name
3. **Public IPs Bucketed**: Coarsened to /24 or /48, then hashed
4. **VPN Adapters Hashed**: Can't identify VPN provider
5. **Machine-Specific Salt**: Prevents cross-device correlation
6. **No Window Titles**: Collector doesn't capture window text
7. **No Keystroke Content**: Only timing, not what was typed
8. **Local-First**: All data written to disk first, backend connection not required

### Security Implications
1. **Process Enumeration**: Collector can see all process names (requires user privileges)
2. **Idle Detection**: Can infer when user is away (privacy concern for monitoring)
3. **Screen Lock Detection**: Reveals user habits (could be sensitive)
4. **Network Monitoring**: Reveals location patterns via WiFi/IP
5. **VPN Usage**: Reveals when user connects to VPN

### Recommendations
- Deploy with user consent
- Disclose what data is collected in clear terms
- Provide opt-out mechanisms
- Secure backend transmission (HTTPS)
- Implement data retention policies
- Consider GDPR/privacy regulations
- Allow users to inspect local spool file

---

## Troubleshooting

### Symptom: No events in spool file
**Check**:
1. Directory created? `spool/` folder exists?
2. Write permissions? Run as admin if needed
3. Collector services running? Check logs for startup messages
4. Antivirus blocking? Check security software logs

### Symptom: Only some event types appear
**Check**:
1. Windows-specific collectors won't work on Linux/Mac
2. Protected processes might cause collection failures
3. Check logs for exceptions
4. Verify collector registered in `Program.cs`

### Symptom: No WiFi SSID events
**Check**:
1. WiFi adapter present and enabled?
2. Connected to a network?
3. Windows Native WiFi API available? (wlanapi.dll)
4. Run as admin (some WiFi queries require elevation)

### Symptom: No display state events
**Check**:
1. DisplayStateListener initialization logs
2. Power notification registration failed? (logged)
3. Message pump thread running? (logged)

### Symptom: Spool file grows too large
**Solutions**:
1. Increase send frequency (reduce `DefaultReportSeconds`)
2. Adjust collector intervals (reduce collection frequency)
3. Implement log rotation (truncate after successful send)
4. Check offset file updating correctly

### Symptom: High CPU usage
**Check**:
1. `ApplicationUsageCollector._pollInterval` too low?
2. Multiple collectors fighting for file lock?
3. Antivirus scanning spool file on every write?
4. WiFi API calls expensive? (rare, but possible)

### Symptom: Public IP events never emit
**Check**:
1. Internet connection available?
2. Firewall blocking HTTPS to api.ipify.org?
3. Corporate proxy blocking API?
4. Check logs for HTTP errors
5. Try manual curl: `curl https://api.ipify.org`

---

## Future Enhancements

### Potential New Collectors
1. **ProcessLifecycleCollector**
   - Process start/stop events
   - Crash detection
   - Elevated process launches
   - Process CPU/memory usage

2. **PerformanceCollector**
   - System CPU usage
   - Memory pressure
   - Battery level
   - Disk I/O patterns

3. **ClipboardCollector** (privacy-sensitive!)
   - Clipboard activity (hashed content)
   - Copy/paste frequency
   - Clipboard data types

4. **USBDeviceCollector**
   - USB device plug/unplug
   - Device types (storage, HID, etc.)
   - Hashed device identifiers

5. **WindowTitleCollector** (privacy-sensitive!)
   - Window title changes (regex-based PII removal)
   - Document names (hashed)
   - Browser tab patterns

### Improvements to Existing Collectors

#### ApplicationUsageCollector
1. **Adaptive Polling**: Slow down when idle, speed up when active
2. **Keyboard/Mouse Activity**: Correlate with app usage
3. **Multi-Monitor Support**: Track which monitor has focus
4. **Per-App CPU/Memory**: Resource usage per application

#### SessionStateCollector
1. **Bluetooth Device Presence**: Detect when user's phone nearby
2. **Camera/Mic Usage**: Privacy-respecting usage indicators
3. **USB Device Events**: Correlate with session state
4. **Multiple Display Tracking**: Per-monitor power state

#### NetworkContextCollector
1. **DNS Query Monitoring**: Track domain categories (hashed)
2. **Bandwidth Usage**: Upload/download patterns
3. **Bluetooth Network**: PAN connections
4. **Cellular Tethering**: Detect mobile hotspot usage
5. **Network Quality Metrics**: Latency, packet loss

#### SpoolFileCollector
1. **Event Batching**: Write multiple events in one I/O operation
2. **Compression**: Compress spool file periodically
3. **Circular Buffer**: Limit spool file size, drop oldest events
4. **Checksum**: Detect file corruption

---

## Performance Characteristics

### CPU Usage (Typical)
- **ApplicationUsageCollector**: ~0.1% (750ms polling)
- **SessionStateCollector**: ~0.05% (2s polling + events)
- **NetworkContextCollector**: ~0.1% (3s local + 60s remote)
- **Total**: < 0.3% CPU on modern system

### Memory Usage
- **Per Collector**: ~5-10 MB (mostly .NET runtime overhead)
- **Spool File**: Grows ~1 KB/minute (depends on activity)
- **Total**: < 50 MB resident

### Disk I/O
- **Write Rate**: ~10-50 events/minute (activity dependent)
- **Event Size**: ~100-200 bytes/event
- **Daily Spool Size**: ~5-15 MB/day

### Network I/O
- **Public IP Check**: 1 HTTP request/minute = ~60 KB/hour
- **Minimal**: All other collectors are local-only
