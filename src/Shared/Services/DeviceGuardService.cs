using System.Runtime.InteropServices;
using EndpointSignalAgent.Bootstrap.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Shared.Services;

/// <summary>
/// Polls workstation idle time and enforces lock/sleep thresholds.
/// Disabled by default — opt in via Agent:DeviceGuard:Enabled = true.
/// </summary>
public sealed class DeviceGuardService : BackgroundService
{
    private readonly ILogger<DeviceGuardService> _logger;
    private readonly IOptions<AgentOptions> _options;

    public DeviceGuardService(ILogger<DeviceGuardService> logger, IOptions<AgentOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = _options.Value.DeviceGuard;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("DeviceGuardService disabled (Agent:DeviceGuard:Enabled=false)");
            return;
        }

        _logger.LogInformation(
            "DeviceGuardService started — lock at {LockSec}s idle, sleep at {SleepSec}s idle, poll every {PollSec}s",
            cfg.IdleLockThresholdSec, cfg.IdleSleepThresholdSec, cfg.PollIntervalSec);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(cfg.PollIntervalSec));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!TryGetIdleSec(out var idleSec)) continue;

                EvaluateIdle(
                    idleSec,
                    cfg,
                    lockWorkstation: () =>
                    {
                        _logger.LogInformation("Idle {Sec}s >= lock threshold {T}s — locking workstation", idleSec, cfg.IdleLockThresholdSec);
                        NativeMethods.LockWorkStation();
                    },
                    sleep: () =>
                    {
                        _logger.LogInformation("Idle {Sec}s >= sleep threshold {T}s — suspending system", idleSec, cfg.IdleSleepThresholdSec);
                        NativeMethods.SetSuspendState(false, false, false);
                    });
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("DeviceGuardService stopped");
    }

    /// <summary>
    /// Pure threshold evaluation — internal for testability.
    /// Sleep threshold takes priority if both are exceeded.
    /// </summary>
    internal static void EvaluateIdle(
        long idleSec,
        AgentOptions.DeviceGuardOptions opts,
        Action lockWorkstation,
        Action sleep)
    {
        if (opts.IdleSleepThresholdSec > 0 && idleSec >= opts.IdleSleepThresholdSec)
        {
            sleep();
            return;
        }

        if (opts.IdleLockThresholdSec > 0 && idleSec >= opts.IdleLockThresholdSec)
        {
            lockWorkstation();
        }
    }

    private static bool TryGetIdleSec(out long idleSec)
    {
        idleSec = 0;
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info)) return false;
        idleSec = (Environment.TickCount64 - (long)info.dwTime) / 1000;
        return idleSec >= 0;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("PowrProf.dll")]
        internal static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll")]
        internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
    }
}
