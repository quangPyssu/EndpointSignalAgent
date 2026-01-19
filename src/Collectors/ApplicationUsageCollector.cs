using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ApplicationUsageCollector : BackgroundService
{
    private readonly ILogger<ApplicationUsageCollector> _logger;
    private readonly string _spoolPath;

    // Tuning knobs (lightweight defaults)
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(750);
    private readonly TimeSpan _switchRateWindow = TimeSpan.FromSeconds(60);

    private ForegroundApp? _current;
    private DateTimeOffset _currentSince;

    private int _switchesInWindow = 0;
    private DateTimeOffset _windowStart;

    public ApplicationUsageCollector(ILogger<ApplicationUsageCollector> logger, string spoolPath = @"spool\signals.jsonl")
    {
        _logger = logger;
        _spoolPath = spoolPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_spoolPath)!);

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
                    await WriteEventAsync(new
                    {
                        ts = now,
                        type = "AppSwitchRate",
                        payload = new
                        {
                            windowSec = (int)_switchRateWindow.TotalSeconds,
                            switches = _switchesInWindow
                        }
                    }, stoppingToken);

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
                    await WriteEventAsync(new
                    {
                        ts = now,
                        type = "ForegroundChanged",
                        payload = new { appKey = fg!.Value.AppKey, category = fg!.Value.Category }
                    }, stoppingToken);

                    await Task.Delay(_pollInterval, stoppingToken);
                    continue;
                }

                if (!fg.Equals(_current))
                {
                    // Close out dwell for previous app
                    var dwellMs = (long)(now - _currentSince).TotalMilliseconds;
                    if (dwellMs < 0) dwellMs = 0;

                    await WriteEventAsync(new
                    {
                        ts = now,
                        type = "AppDwell",
                        payload = new
                        {
                            appKey = _current!.Value.AppKey,
                            category = _current!.Value.Category,
                            durationMs = dwellMs
                        }
                    }, stoppingToken);

                    // Switch bookkeeping
                    _switchesInWindow++;

                    // Update current app
                    _current = fg;
                    _currentSince = now;

                    await WriteEventAsync(new
                    {
                        ts = now,
                        type = "ForegroundChanged",
                        payload = new { appKey = fg!.Value.AppKey, category = fg!.Value.Category }
                    }, stoppingToken);
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

                await WriteEventAsync(new
                {
                    ts = now,
                    type = "AppDwell",
                    payload = new
                    {
                        appKey = _current!.Value.AppKey,
                        category = _current!.Value.Category,
                        durationMs = dwellMs,
                        reason = "shutdown_flush"
                    }
                }, CancellationToken.None);
            }
        }
        catch { /* best-effort */ }

        _logger.LogInformation("ApplicationUsageCollector stopped.");
    }

    private async Task WriteEventAsync(object evt, CancellationToken ct)
    {
        // Keep it simple & compatible with JSONL spooling
        var line = JsonSerializer.Serialize(evt);
        await File.AppendAllTextAsync(_spoolPath, line + Environment.NewLine, ct);
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
            var category = Categorize(exeName);

            return new ForegroundApp(appKey, category);
        }
        catch
        {
            return null;
        }
    }

    private static string Categorize(string exeName)
    {
        // Keep this tiny + effective: map common apps; fallback to "Other"
        exeName = exeName.ToLowerInvariant();

        // Browsers
        if (exeName is "chrome" or "msedge" or "firefox" or "brave") return "Browser";

        // IDE / Dev
        if (exeName is "devenv" or "rider64" or "code" or "idea64" or "clion64") return "IDE";
        if (exeName is "cmd" or "powershell" or "pwsh" or "windowsTerminal" or "wt") return "Terminal";

        // Communication
        if (exeName is "slack" or "teams" or "discord" or "zoom" or "skype") return "Comms";

        // Office/Productivity
        if (exeName is "winword" or "excel" or "powerpnt" or "onenote") return "Office";

        // Media
        if (exeName is "spotify" or "vlc" or "musicbee") return "Media";

        return "Other";
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
