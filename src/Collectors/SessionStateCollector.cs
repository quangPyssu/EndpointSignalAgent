using System.Runtime.InteropServices;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Collectors; // where SpoolFileCollector lives
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Management;

public sealed class SessionStateCollector : BackgroundService
{
    private readonly ILogger<SessionStateCollector> _logger;
    private readonly string _spoolPath;

    private int _lastIdleBucketSec = -1;

    public SessionStateCollector(ILogger<SessionStateCollector> logger)
    {
        _logger = logger;
        _spoolPath = @"spool\signals.jsonl";
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");

        StartSessionWatcher();

        // Run idle sampler loop in the same service lifetime
        return IdleLoop(stoppingToken);
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

            await WriteAsync(type, new Dictionary<string, string>
            {
                //["sessionId"] = e.SessionId.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session watcher event processing failed");
        }
    }

    private async Task IdleLoop(CancellationToken ct)
    {
        _logger.LogInformation("SessionStateCollector: idle loop started");

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

                    await WriteAsync(SignalEventType.IdleSample, new Dictionary<string, string>
                    {
                        ["idleMs"] = idleMs.ToString(),
                        ["idleBucketSec"] = bucketSec.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idle loop iteration failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task WriteAsync(SignalEventType type, Dictionary<string, string> payload)
    {
        using var writer = new SpoolFileCollector(_spoolPath);
        await writer.WriteAsync(new SignalEvent(
            DateTimeOffset.UtcNow,
            type,
            payload));
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch { /* ignore */ }

        return base.StopAsync(cancellationToken);
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
}
