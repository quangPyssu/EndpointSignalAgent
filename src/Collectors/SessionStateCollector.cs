using System.Runtime.InteropServices;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Collectors;
using Microsoft.Extensions.Logging;

public sealed class SessionStateCollector : SignalCollectorBase
{
    private readonly ILogger<SessionStateCollector> _logger;

    private int _lastIdleBucketSec = -1;
    private bool? _lastScreenSaverRunning = null;
    private int? _lastDisplayState = null; // 0=off, 1=on, 2=dimmed

    private DisplayStateListener? _displayListener;

    public SessionStateCollector(ILogger<SessionStateCollector> logger)
        : base(@"spool\signals.jsonl")
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");

        StartSessionWatcher();
        StartDisplayStateWatcher();

        // Run idle + screensaver sampler loop in the same service lifetime
        return IdleAndScreenSaverLoop(stoppingToken);
    }

    private void StartSessionWatcher()
    {
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _logger.LogInformation("SessionStateCollector: session watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start session watcher");
            throw;
        }
    }

    private void StartDisplayStateWatcher()
    {
        try
        {
            _displayListener = new DisplayStateListener(_logger, OnDisplayStateChanged);
            _displayListener.Start();
            _logger.LogInformation("SessionStateCollector: display state watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start display state watcher (continuing without it)");
        }
    }

    private async void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        try
        {
            var type = e.Reason switch
            {
                Microsoft.Win32.SessionSwitchReason.SessionLock => SignalEventType.SessionLock,
                Microsoft.Win32.SessionSwitchReason.SessionUnlock => SignalEventType.SessionUnlock,
                _ => SignalEventType.Unknown
            };

            if (type == SignalEventType.Unknown) return;

            await WriteSignalAsync(type, new Dictionary<string, string>
            {
                ["reason"] = e.Reason.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session watcher event processing failed");
        }
    }

    private void OnDisplayStateChanged(DisplayStateChange change)
    {
        // Called on a window/message thread; do not block it.
        if (_lastDisplayState == change.State)
            return;

        _lastDisplayState = change.State;

        var type = change.State switch
        {
            0 => SignalEventType.DisplayOff,
            1 => SignalEventType.DisplayOn,
            2 => SignalEventType.DisplayDimmed,
            _ => SignalEventType.Unknown
        };

        if (type == SignalEventType.Unknown) return;

        _ = Task.Run(async () =>
        {
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write display state signal");
            }
        });
    }

    private async Task IdleAndScreenSaverLoop(CancellationToken ct)
    {
        _logger.LogInformation("SessionStateCollector: idle/screensaver loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var idleMs = GetIdleMilliseconds();
                var idleSec = (int)(idleMs / 1000);

                // Bucket to reduce spam (0,5,10,15,...)
                var bucketSec = (idleSec / 5) * 5;

                if (bucketSec != _lastIdleBucketSec)
                {
                    _lastIdleBucketSec = bucketSec;

                    await WriteSignalAsync(SignalEventType.IdleSample, new Dictionary<string, string>
                    {
                        ["idleMs"] = idleMs.ToString(),
                        ["idleBucketSec"] = bucketSec.ToString()
                    });
                }

                // Screen saver running state transitions
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
                        }
                    );
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
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch { /* ignore */ }

        try
        {
            _displayListener?.Dispose();
            _displayListener = null;
        }
        catch { /* ignore */ }

        return base.StopAsync(cancellationToken);
    }

    // ---- Screen saver state (polling) ----

    private const uint SPI_GETSCREENSAVERRUNNING = 114;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);

    private static bool? TryGetScreenSaverRunning()
    {
        if (SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, out var running, 0))
            return running;

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

    private static ulong GetIdleMilliseconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
            return 0;

        // Handle TickCount wrap by unsigned subtraction
        var now = unchecked((uint)Environment.TickCount);
        var idleMs = unchecked(now - lii.dwTime);
        return idleMs;
    }

    // ---- Display on/off/dim via power broadcast ----

    private readonly record struct DisplayStateChange(int State, string Source);

    private sealed class DisplayStateListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Action<DisplayStateChange> _onChange;

        private Thread? _thread;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _notifyHandle = IntPtr.Zero;
        private WndProcDelegate? _wndProc; // keep delegate alive
        private readonly ManualResetEventSlim _ready = new(false);

        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int WM_CLOSE = 0x0010;

        private static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
            new("6FE69556-704A-47A0-8F24-C28D936FDA47");

        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        public DisplayStateListener(ILogger logger, Action<DisplayStateChange> onChange)
        {
            _logger = logger;
            _onChange = onChange;
        }

        public void Start()
        {
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "DisplayStateListener"
            };

            // Window message pump likes STA
            try { _thread.SetApartmentState(ApartmentState.STA); } catch { /* ignore */ }

            _thread.Start();

            // Don't block forever if something goes wrong
            _ready.Wait(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                    PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch { /* ignore */ }

            try
            {
                if (_thread != null && _thread.IsAlive)
                    _thread.Join(TimeSpan.FromSeconds(2));
            }
            catch { /* ignore */ }

            _ready.Dispose();
        }

        private void ThreadMain()
        {
            try
            {
                var hInstance = GetModuleHandle(null);

                var className = "EndpointSignalAgent_DisplayListener_" + Guid.NewGuid().ToString("N");

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
                    _logger.LogWarning("DisplayStateListener: RegisterClassEx failed (err={err})", Marshal.GetLastWin32Error());
                    _ready.Set();
                    return;
                }

                _hwnd = CreateWindowEx(
                    0,
                    className,
                    className,
                    0,
                    0, 0, 0, 0,
                    HWND_MESSAGE,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    _logger.LogWarning("DisplayStateListener: CreateWindowEx failed (err={err})", Marshal.GetLastWin32Error());
                    _ready.Set();
                    return;
                }

                // Create a local copy of the GUID to pass by ref
                var displayStateGuid = GUID_CONSOLE_DISPLAY_STATE;
                _notifyHandle = RegisterPowerSettingNotification(_hwnd, ref displayStateGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_notifyHandle == IntPtr.Zero)
                {
                    _logger.LogWarning("DisplayStateListener: RegisterPowerSettingNotification failed (err={err})", Marshal.GetLastWin32Error());
                    // keep running; just won't get events
                }

                _ready.Set();

                // Message loop
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DisplayStateListener: thread crashed");
            }
            finally
            {
                try
                {
                    if (_notifyHandle != IntPtr.Zero)
                        UnregisterPowerSettingNotification(_notifyHandle);
                }
                catch { /* ignore */ }

                try
                {
                    if (_hwnd != IntPtr.Zero)
                        DestroyWindow(_hwnd);
                }
                catch { /* ignore */ }
            }
        }

        private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if ((int)msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE)
            {
                try
                {
                    var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                    if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE && setting.DataLength >= 4)
                    {
                        var dataOffset = Marshal.SizeOf<POWERBROADCAST_SETTING>();
                        var state = Marshal.ReadInt32(lParam, dataOffset); // 0=Off, 1=On, 2=Dimmed

                        _onChange(new DisplayStateChange(state, "GUID_CONSOLE_DISPLAY_STATE"));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DisplayStateListener: failed to parse power broadcast");
                }

                return new IntPtr(1);
            }

            if ((int)msg == WM_CLOSE)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
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
            // Followed by Data bytes
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
    }
}
