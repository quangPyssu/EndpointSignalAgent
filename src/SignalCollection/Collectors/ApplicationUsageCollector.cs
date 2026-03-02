using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Utilities;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EndpointSignalAgent.SignalCollection.Collectors;

public sealed class ApplicationUsageCollector : SignalCollectorBase
{
    private readonly ILogger<ApplicationUsageCollector> _logger;

    // Tuning knobs (lightweight defaults)
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(750);
    private readonly TimeSpan _switchRateWindow = TimeSpan.FromSeconds(60);

    private ForegroundApp? _current;
    private DateTimeOffset _currentSince;

    private int _switchesInWindow = 0;
    private DateTimeOffset _windowStart;

    public ApplicationUsageCollector(
        ILogger<ApplicationUsageCollector> logger,
        ISignalBroadcaster broadcaster)
        : base(@"spool\signals.jsonl", broadcaster)
    {
        _logger = logger;

        _windowStart = DateTimeOffset.UtcNow;
        _currentSince = _windowStart;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ApplicationUsageCollector started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                // Emit switch-rate once per window
                if (now - _windowStart >= _switchRateWindow)
                {
                    await WriteSignalAsync(SignalEventType.AppSwitchRate, new Dictionary<string, string>
                    {
                        ["windowSec"] = ((int)_switchRateWindow.TotalSeconds).ToString(),
                        ["switches"] = _switchesInWindow.ToString()
                    });

                    _switchesInWindow = 0;
                    _windowStart = now;
                }

                var fg = TryGetForegroundApp();
                if (fg is null)
                {
                    // no foreground window (lock screen, secure desktop, etc.)
                    await Task.Delay(_pollInterval, stoppingToken);
                    continue;
                }

                if (_current is null)
                {
                    _current = fg;
                    _currentSince = now;

                    // Optional: emit initial context
                    await WriteSignalAsync(SignalEventType.ForegroundAppChanged, new Dictionary<string, string>
                    {
                        ["appKey"] = fg!.Value.AppKey,
                        ["category"] = fg!.Value.Category
                    });

                    await Task.Delay(_pollInterval, stoppingToken);
                    continue;
                }

                if (!fg.Equals(_current))
                {
                    // Close out dwell for previous app
                    var dwellMs = (long)(now - _currentSince).TotalMilliseconds;
                    if (dwellMs < 0) dwellMs = 0;

                    await WriteSignalAsync(SignalEventType.AppDwell, new Dictionary<string, string>
                    {
                        ["appKey"] = _current!.Value.AppKey,
                        ["category"] = _current!.Value.Category,
                        ["durationMs"] = dwellMs.ToString()
                    });

                    // Switch bookkeeping
                    _switchesInWindow++;

                    // Update current app
                    _current = fg;
                    _currentSince = now;

                    await WriteSignalAsync(SignalEventType.ForegroundAppChanged, new Dictionary<string, string>
                    {
                        ["appKey"] = fg!.Value.AppKey,
                        ["category"] = fg!.Value.Category
                    });
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplicationUsageCollector loop error.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        // On shutdown: flush the last dwell slice (optional)
        try
        {
            if (_current is not null)
            {
                var now = DateTimeOffset.UtcNow;
                var dwellMs = (long)(now - _currentSince).TotalMilliseconds;

                await WriteSignalAsync(SignalEventType.AppDwell, new Dictionary<string, string>
                {
                    ["appKey"] = _current!.Value.AppKey,
                    ["category"] = _current!.Value.Category,
                    ["durationMs"] = dwellMs.ToString(),
                    ["reason"] = "shutdown_flush"
                });
            }
        }
        catch { /* best-effort */ }

        _logger.LogInformation("ApplicationUsageCollector stopped.");
    }

    private static ForegroundApp? TryGetForegroundApp()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        try
        {
            using var p = Process.GetProcessById((int)pid);

            // exe name is cheap; MainModule.FileName can throw without privileges
            var exeName = p.ProcessName;

            string? fullPath = null;
            try { fullPath = p.MainModule?.FileName; } catch { /* ignore */ }

            var appKey = HashStable(fullPath ?? exeName);
            var category = ApplicationCategorizer.Categorize(exeName);

            return new ForegroundApp(appKey, category);
        }
        catch
        {
            return null;
        }
    }

    private static string HashStable(string input)
    {
        // Privacy-preserving key: stable but not reversible
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        // short-ish printable token
        return Convert.ToHexString(bytes.AsSpan(0, 12)); // 24 hex chars
    }

    private readonly record struct ForegroundApp(string AppKey, string Category);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
