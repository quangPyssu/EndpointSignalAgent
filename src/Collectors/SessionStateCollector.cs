using System.Management; // NuGet: System.Management
using System.Runtime.InteropServices;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Collectors; // where SpoolFileCollector lives
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class SessionStateCollector : BackgroundService
{
    private readonly ILogger<SessionStateCollector> _logger;
    private readonly string _spoolPath;

    private ManagementEventWatcher? _sessionWatcher;

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
        // Reason 7 = lock, 8 = unlock
        var query = new WqlEventQuery("SELECT * FROM Win32_SessionChangeEvent WHERE Reason = 7 OR Reason = 8");
        _sessionWatcher = new ManagementEventWatcher(query);

        _sessionWatcher.EventArrived += async (_, e) =>
        {
            try
            {
                var reason = Convert.ToInt32(e.NewEvent.Properties["Reason"].Value);
                var sessionId = Convert.ToString(e.NewEvent.Properties["SessionID"].Value) ?? "";

                var type = reason switch
                {
                    7 => SignalEventType.SessionLock,
                    8 => SignalEventType.SessionUnlock,
                    _ => SignalEventType.Unknown
                };

                if (type == SignalEventType.Unknown) return;

                await WriteAsync(type, new Dictionary<string, string>
                {
                    ["sessionId"] = sessionId
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session watcher event processing failed");
            }
        };

        _sessionWatcher.Start();
        _logger.LogInformation("SessionStateCollector: session watcher started");
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
            _sessionWatcher?.Stop();
            _sessionWatcher?.Dispose();
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
