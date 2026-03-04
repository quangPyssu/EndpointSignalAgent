using System.Runtime.InteropServices;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.SignalCollection.Collectors;

public sealed class SessionStateCollector : SignalCollectorBase
{
    private readonly ILogger<SessionStateCollector> _logger;
    private readonly Channel<QueuedSignal> _signalQueue = Channel.CreateUnbounded<QueuedSignal>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly DebouncedStateTracker<bool> _sessionLockDebouncer = new(confirmationsRequired: 2, settleTime: TimeSpan.FromSeconds(2));
    private readonly DebouncedStateTracker<int> _displayDebouncer = new(confirmationsRequired: 2, settleTime: TimeSpan.FromSeconds(2));
    private readonly DebouncedStateTracker<bool> _screenSaverDebouncer = new(confirmationsRequired: 2, settleTime: TimeSpan.FromSeconds(2));
    private readonly DebouncedStateTracker<string> _presenceDebouncer = new(confirmationsRequired: 2, settleTime: TimeSpan.FromSeconds(2));

    private int _lastIdleBucketSec = -1;
    private DateTimeOffset _lastIdleSampleUtc = DateTimeOffset.MinValue;
    private bool _systemEventsSessionWatcherEnabled;
    private DateTimeOffset _lastWtsLockEventUtc = DateTimeOffset.MinValue;

    private bool _isSessionLocked;
    private string? _userPresence;

    private SessionEventWindowListener? _sessionAndDisplayListener;

    private readonly record struct QueuedSignal(SignalEventType Type, Dictionary<string, string> Payload);
    private readonly record struct DisplayStateChange(int State, string Source);
    private readonly record struct PresenceChange(string Presence, string Source);
    private readonly record struct SessionStateChange(bool IsLocked, string Source, string Reason);

    public SessionStateCollector(
        ILogger<SessionStateCollector> logger,
        ISignalBroadcaster broadcaster)
        : base(@"spool\signals.jsonl", broadcaster)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");

        var queuePumpTask = ProcessSignalQueueAsync(stoppingToken);

        StartSessionWatcherFallback();
        StartSessionAndDisplayWatcher();

        EmitInitialDisplayState();
        EmitInitialIdleAndScreenSaverState();

        try
        {
            await IdleAndScreenSaverLoop(stoppingToken);
        }
        finally
        {
            _signalQueue.Writer.TryComplete();
            await queuePumpTask;
        }
    }

    private void EmitInitialDisplayState()
    {
        EnqueueSignal(SignalEventType.DisplayOn, new Dictionary<string, string>
        {
            ["displayState"] = "On",
            ["source"] = "initial_unknown",
            ["confidence"] = "low",
            ["reason"] = "no_power_event_yet"
        });
    }

    private void EmitInitialIdleAndScreenSaverState()
    {
        var now = DateTimeOffset.UtcNow;

        if (TryGetIdleMilliseconds(out var idleMs))
        {
            var idleSec = (int)(idleMs / 1000);
            var bucketStep = GetAdaptiveIdleBucketStepSeconds(idleSec);
            var bucketSec = (idleSec / bucketStep) * bucketStep;
            _lastIdleBucketSec = bucketSec;

            var payload = new Dictionary<string, string>
            {
                ["idleMs"] = idleMs.ToString(),
                ["idleBucketSec"] = bucketSec.ToString(),
                ["idleStatus"] = "ok"
            };

            if (!string.IsNullOrWhiteSpace(_userPresence))
            {
                payload["userPresence"] = _userPresence;
                payload["presenceSource"] = "GUID_SESSION_USER_PRESENCE";
            }

            EnqueueSignal(SignalEventType.IdleSample, payload);
        }
        else
        {
            EnqueueSignal(SignalEventType.IdleSample, new Dictionary<string, string>
            {
                ["idleMs"] = "-1",
                ["idleBucketSec"] = "-1",
                ["idleStatus"] = "api_fail"
            });
        }

        var screenSaverRunning = TryGetScreenSaverRunning();
        if (screenSaverRunning.HasValue)
        {
            if (_screenSaverDebouncer.TryTransition(screenSaverRunning.Value, now, out var stableScreenSaver))
            {
                EnqueueSignal(
                    stableScreenSaver ? SignalEventType.ScreenSaverOn : SignalEventType.ScreenSaverOff,
                    new Dictionary<string, string>
                    {
                        ["running"] = stableScreenSaver ? "true" : "false",
                        ["screensaverStatus"] = "ok"
                    });
            }
        }
        else
        {
            EnqueueSignal(SignalEventType.IdleSample, new Dictionary<string, string>
            {
                ["idleMs"] = "-1",
                ["idleBucketSec"] = "-1",
                ["idleStatus"] = "ok",
                ["screensaverStatus"] = "api_fail"
            });
        }

        _lastIdleSampleUtc = now;
    }

    private void StartSessionWatcherFallback()
    {
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _systemEventsSessionWatcherEnabled = true;
            _logger.LogInformation("SessionStateCollector: SystemEvents session watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionStateCollector: SystemEvents session watcher unavailable; continuing with WTS-only path when possible");
        }
    }

    private void StartSessionAndDisplayWatcher()
    {
        try
        {
            _sessionAndDisplayListener = new SessionEventWindowListener(
                _logger,
                OnDisplayStateChanged,
                OnSessionStateChanged,
                OnUserPresenceChanged);
            _sessionAndDisplayListener.Start();
            _logger.LogInformation("SessionStateCollector: session/display hidden-window watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start hidden-window session/display watcher (continuing with fallback paths)");
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (!_systemEventsSessionWatcherEnabled)
        {
            return;
        }

        try
        {
            var eventType = e.Reason switch
            {
                Microsoft.Win32.SessionSwitchReason.SessionLock => SignalEventType.SessionLock,
                Microsoft.Win32.SessionSwitchReason.SessionUnlock => SignalEventType.SessionUnlock,
                _ => SignalEventType.Unknown
            };

            if (eventType == SignalEventType.Unknown)
            {
                return;
            }

            // Prefer WTS when both are available.
            if ((DateTimeOffset.UtcNow - _lastWtsLockEventUtc) < TimeSpan.FromSeconds(3))
            {
                return;
            }

            var sessionChange = new SessionStateChange(
                IsLocked: eventType == SignalEventType.SessionLock,
                Source: "SystemEvents",
                Reason: e.Reason.ToString());
            OnSessionStateChanged(sessionChange);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session watcher fallback event processing failed");
        }
    }

    private void OnSessionStateChanged(SessionStateChange change)
    {
        if (change.Source == "WTS")
        {
            _lastWtsLockEventUtc = DateTimeOffset.UtcNow;
        }

        if (!_sessionLockDebouncer.TryTransition(change.IsLocked, DateTimeOffset.UtcNow, out var stableLockState))
        {
            return;
        }

        _isSessionLocked = stableLockState;

        EnqueueSignal(
            stableLockState ? SignalEventType.SessionLock : SignalEventType.SessionUnlock,
            new Dictionary<string, string>
            {
                ["source"] = change.Source,
                ["reason"] = change.Reason
            });
    }

    private void OnDisplayStateChanged(DisplayStateChange change)
    {
        if (!_displayDebouncer.TryTransition(change.State, DateTimeOffset.UtcNow, out var stableState))
        {
            return;
        }

        var type = stableState switch
        {
            0 => SignalEventType.DisplayOff,
            1 => SignalEventType.DisplayOn,
            2 => SignalEventType.DisplayDimmed,
            _ => SignalEventType.Unknown
        };

        if (type == SignalEventType.Unknown)
        {
            return;
        }

        var payload = new Dictionary<string, string>
        {
            ["displayState"] = stableState switch
            {
                0 => "Off",
                1 => "On",
                2 => "Dimmed",
                _ => "Unknown"
            },
            ["source"] = change.Source,
            ["confidence"] = "high"
        };

        if (!string.IsNullOrWhiteSpace(_userPresence))
        {
            payload["userPresence"] = _userPresence;
            payload["presenceSource"] = "GUID_SESSION_USER_PRESENCE";
        }

        EnqueueSignal(type, payload);
    }

    private void OnUserPresenceChanged(PresenceChange change)
    {
        if (!_presenceDebouncer.TryTransition(change.Presence, DateTimeOffset.UtcNow, out var stablePresence))
        {
            return;
        }

        _userPresence = stablePresence;

        // Keep downstream compatibility by attaching presence to existing signal types (idle samples).
        EnqueueSignal(SignalEventType.IdleSample, new Dictionary<string, string>
        {
            ["idleMs"] = "-1",
            ["idleBucketSec"] = "-1",
            ["idleStatus"] = "presence_update",
            ["userPresence"] = stablePresence,
            ["presenceSource"] = change.Source
        });
    }

    private async Task IdleAndScreenSaverLoop(CancellationToken ct)
    {
        _logger.LogInformation("SessionStateCollector: idle/screensaver loop started");

        // Emit initial idle and screensaver state
        await EmitInitialIdleAndScreenSaverState();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var reducedCadence = _isSessionLocked || string.Equals(_userPresence, "away", StringComparison.OrdinalIgnoreCase);
                var cadence = reducedCadence ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2);

                if ((now - _lastIdleSampleUtc) >= cadence)
                {
                    _lastIdleSampleUtc = now;

                    if (TryGetIdleMilliseconds(out var idleMs))
                    {
                        var idleSec = (int)(idleMs / 1000);
                        var bucketStep = GetAdaptiveIdleBucketStepSeconds(idleSec);
                        var bucketSec = (idleSec / bucketStep) * bucketStep;

                        if (bucketSec != _lastIdleBucketSec)
                        {
                            _lastIdleBucketSec = bucketSec;
                            var payload = new Dictionary<string, string>
                            {
                                ["idleMs"] = idleMs.ToString(),
                                ["idleBucketSec"] = bucketSec.ToString(),
                                ["idleStatus"] = "ok"
                            };

                            if (!string.IsNullOrWhiteSpace(_userPresence))
                            {
                                payload["userPresence"] = _userPresence;
                                payload["presenceSource"] = "GUID_SESSION_USER_PRESENCE";
                            }

                            EnqueueSignal(SignalEventType.IdleSample, payload);
                        }
                    }
                    else
                    {
                        EnqueueSignal(SignalEventType.IdleSample, new Dictionary<string, string>
                        {
                            ["idleMs"] = "-1",
                            ["idleBucketSec"] = "-1",
                            ["idleStatus"] = "api_fail"
                        });
                    }
                }

                var ssRunning = TryGetScreenSaverRunning();
                if (ssRunning.HasValue)
                {
                    if (_screenSaverDebouncer.TryTransition(ssRunning.Value, now, out var stableScreenSaver))
                    {
                        EnqueueSignal(
                            stableScreenSaver ? SignalEventType.ScreenSaverOn : SignalEventType.ScreenSaverOff,
                            new Dictionary<string, string>
                            {
                                ["running"] = stableScreenSaver ? "true" : "false",
                                ["screensaverStatus"] = "ok"
                            });
                    }
                }
                else
                {
                    EnqueueSignal(SignalEventType.IdleSample, new Dictionary<string, string>
                    {
                        ["idleMs"] = "-1",
                        ["idleBucketSec"] = "-1",
                        ["idleStatus"] = "ok",
                        ["screensaverStatus"] = "api_fail"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idle/screensaver loop iteration failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_systemEventsSessionWatcherEnabled)
            {
                Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            _sessionAndDisplayListener?.Dispose();
            _sessionAndDisplayListener = null;
        }
        catch
        {
            // ignore
        }

        return base.StopAsync(cancellationToken);
    }

    private void EnqueueSignal(SignalEventType type, Dictionary<string, string> payload)
    {
        if (!_signalQueue.Writer.TryWrite(new QueuedSignal(type, payload)))
        {
            _logger.LogWarning("SessionStateCollector: failed to enqueue signal type {Type}", type);
        }
    }

    private async Task ProcessSignalQueueAsync(CancellationToken ct)
    {
        await foreach (var signal in _signalQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await WriteSignalAsync(signal.Type, signal.Payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionStateCollector: failed to write queued signal type {Type}", signal.Type);
            }
        }
    }

    internal static int GetAdaptiveIdleBucketStepSeconds(int idleSec)
    {
        if (idleSec < 120)
        {
            return 5;
        }

        if (idleSec <= 600)
        {
            return 15;
        }

        return 60;
    }

    // ---- Screen saver state (polling) ----

    private const uint SPI_GETSCREENSAVERRUNNING = 114;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);

    private static bool? TryGetScreenSaverRunning()
    {
        if (SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, out var running, 0))
        {
            return running;
        }

        return null;
    }

    // ---- Win32 idle time ----

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static bool TryGetIdleMilliseconds(out ulong idleMs)
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
        {
            idleMs = 0;
            return false;
        }

        var now = unchecked((uint)Environment.TickCount);
        idleMs = unchecked(now - lii.dwTime);
        return true;
    }

    private sealed class DebouncedStateTracker<T> where T : notnull
    {
        private readonly int _confirmationsRequired;
        private readonly TimeSpan _settleTime;
        private readonly object _sync = new();

        private bool _hasStable;
        private T? _stableState;

        private bool _hasCandidate;
        private T? _candidateState;
        private int _candidateConfirmations;
        private DateTimeOffset _candidateSinceUtc;

        public DebouncedStateTracker(int confirmationsRequired, TimeSpan settleTime)
        {
            _confirmationsRequired = Math.Max(1, confirmationsRequired);
            _settleTime = settleTime;
        }

        public bool TryTransition(T observedState, DateTimeOffset nowUtc, out T stableState)
        {
            lock (_sync)
            {
                if (!_hasStable)
                {
                    _stableState = observedState;
                    _hasStable = true;
                    _hasCandidate = false;
                    stableState = observedState;
                    return true;
                }

                if (EqualityComparer<T>.Default.Equals(observedState, _stableState!))
                {
                    _hasCandidate = false;
                    stableState = _stableState!;
                    return false;
                }

                if (!_hasCandidate || !EqualityComparer<T>.Default.Equals(observedState, _candidateState!))
                {
                    _candidateState = observedState;
                    _candidateConfirmations = 1;
                    _candidateSinceUtc = nowUtc;
                    _hasCandidate = true;
                    stableState = _stableState!;
                    return false;
                }

                _candidateConfirmations++;

                var shouldCommit =
                    _candidateConfirmations >= _confirmationsRequired ||
                    (nowUtc - _candidateSinceUtc) >= _settleTime;

                if (!shouldCommit)
                {
                    stableState = _stableState!;
                    return false;
                }

                _stableState = _candidateState;
                _hasCandidate = false;
                stableState = _stableState!;
                return true;
            }
        }
    }

    // ---- Hidden window for power + WTS session events ----

    private sealed class SessionEventWindowListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Action<DisplayStateChange> _onDisplayChange;
        private readonly Action<SessionStateChange> _onSessionChange;
        private readonly Action<PresenceChange> _onPresenceChange;

        private Thread? _thread;
        private IntPtr _hwnd = IntPtr.Zero;
        private readonly List<IntPtr> _powerNotificationHandles = new();
        private bool _wtsRegistered;
        private WndProcDelegate? _wndProc;
        private readonly ManualResetEventSlim _ready = new(false);

        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int WM_CLOSE = 0x0010;

        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const int NOTIFY_FOR_THIS_SESSION = 0;

        private const int WTS_SESSION_LOCK = 0x7;
        private const int WTS_SESSION_UNLOCK = 0x8;

        private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
        private static readonly Guid GUID_MONITOR_POWER_ON = new("02731015-4510-4526-99E6-E5A17EBD1AEA");
        private static readonly Guid GUID_SESSION_USER_PRESENCE = new("3C0F4548-C03F-4C4D-B9F2-237EDE686376");

        public SessionEventWindowListener(
            ILogger logger,
            Action<DisplayStateChange> onDisplayChange,
            Action<SessionStateChange> onSessionChange,
            Action<PresenceChange> onPresenceChange)
        {
            _logger = logger;
            _onDisplayChange = onDisplayChange;
            _onSessionChange = onSessionChange;
            _onPresenceChange = onPresenceChange;
        }

        public void Start()
        {
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "SessionEventWindowListener"
            };

            try { _thread.SetApartmentState(ApartmentState.STA); } catch { }

            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }

            try
            {
                if (_thread is { IsAlive: true })
                {
                    _thread.Join(TimeSpan.FromSeconds(2));
                }
            }
            catch { }

            _ready.Dispose();
        }

        private void ThreadMain()
        {
            try
            {
                var hInstance = GetModuleHandle(null);
                var className = "EndpointSignalAgent_SessionEventWindow_" + Guid.NewGuid().ToString("N");

                _wndProc = WndProcImpl;

                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = hInstance,
                    lpszClassName = className
                };

                if (RegisterClassEx(ref wc) == 0)
                {
                    _logger.LogWarning("SessionEventWindowListener: RegisterClassEx failed (err={err})", Marshal.GetLastWin32Error());
                    _ready.Set();
                    return;
                }

                _hwnd = CreateWindowEx(
                    0,
                    className,
                    className,
                    0,
                    0,
                    0,
                    0,
                    0,
                    HWND_MESSAGE,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    _logger.LogWarning("SessionEventWindowListener: CreateWindowEx failed (err={err})", Marshal.GetLastWin32Error());
                    _ready.Set();
                    return;
                }

                RegisterPowerGuid(GUID_CONSOLE_DISPLAY_STATE);
                RegisterPowerGuid(GUID_MONITOR_POWER_ON);
                RegisterPowerGuid(GUID_SESSION_USER_PRESENCE);

                _wtsRegistered = WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
                if (!_wtsRegistered)
                {
                    _logger.LogWarning("SessionEventWindowListener: WTSRegisterSessionNotification failed (err={err})", Marshal.GetLastWin32Error());
                }

                _ready.Set();

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionEventWindowListener: thread crashed");
            }
            finally
            {
                try
                {
                    if (_wtsRegistered && _hwnd != IntPtr.Zero)
                    {
                        WTSUnRegisterSessionNotification(_hwnd);
                    }
                }
                catch { }

                foreach (var handle in _powerNotificationHandles)
                {
                    try { UnregisterPowerSettingNotification(handle); } catch { }
                }

                try
                {
                    if (_hwnd != IntPtr.Zero)
                    {
                        DestroyWindow(_hwnd);
                    }
                }
                catch { }
            }
        }

        private void RegisterPowerGuid(Guid guid)
        {
            var guidCopy = guid;
            var notifyHandle = RegisterPowerSettingNotification(_hwnd, ref guidCopy, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (notifyHandle == IntPtr.Zero)
            {
                _logger.LogWarning("SessionEventWindowListener: RegisterPowerSettingNotification failed for {guid} (err={err})", guid, Marshal.GetLastWin32Error());
                return;
            }

            _powerNotificationHandles.Add(notifyHandle);
        }

        private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if ((int)msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE)
            {
                ParsePowerSettingChange(lParam);
                return new IntPtr(1);
            }

            if ((int)msg == WM_WTSSESSION_CHANGE)
            {
                ParseSessionChange(wParam);
                return IntPtr.Zero;
            }

            if ((int)msg == WM_CLOSE)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ParseSessionChange(IntPtr wParam)
        {
            var reasonCode = wParam.ToInt32();
            var reasonName = reasonCode switch
            {
                WTS_SESSION_LOCK => nameof(WTS_SESSION_LOCK),
                WTS_SESSION_UNLOCK => nameof(WTS_SESSION_UNLOCK),
                _ => $"WTS_{reasonCode}"
            };

            if (reasonCode == WTS_SESSION_LOCK)
            {
                _onSessionChange(new SessionStateChange(true, "WTS", reasonName));
            }
            else if (reasonCode == WTS_SESSION_UNLOCK)
            {
                _onSessionChange(new SessionStateChange(false, "WTS", reasonName));
            }
        }

        private void ParsePowerSettingChange(IntPtr lParam)
        {
            try
            {
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.DataLength < 4)
                {
                    return;
                }

                var dataOffset = Marshal.SizeOf<POWERBROADCAST_SETTING>();
                var rawState = Marshal.ReadInt32(lParam, dataOffset);

                if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    _onDisplayChange(new DisplayStateChange(rawState, "GUID_CONSOLE_DISPLAY_STATE"));
                    return;
                }

                if (setting.PowerSetting == GUID_MONITOR_POWER_ON)
                {
                    // 0 = monitor off, 1 = monitor on
                    _onDisplayChange(new DisplayStateChange(rawState == 0 ? 0 : 1, "GUID_MONITOR_POWER_ON"));
                    return;
                }

                if (setting.PowerSetting == GUID_SESSION_USER_PRESENCE)
                {
                    var presence = rawState switch
                    {
                        0 => "present",
                        1 => "away",
                        _ => "away"
                    };

                    _onPresenceChange(new PresenceChange(presence, "GUID_SESSION_USER_PRESENCE"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionEventWindowListener: failed to parse power broadcast");
            }
        }

        // ---- Native ----

        private static readonly IntPtr HWND_MESSAGE = new(-3);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public int DataLength;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, [In] ref Guid PowerSettingGuid, int Flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
    }
}
