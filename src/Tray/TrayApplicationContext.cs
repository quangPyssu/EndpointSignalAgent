using EndpointSignalAgent.Bootstrap;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace EndpointSignalAgent.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _pauseResumeMenuItem;
    private readonly ILoggerFactory _trayLoggerFactory;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly SynchronizationContext _uiContext;

    private IHost? _host;
    private ICollectionControl? _collectionControl;
    private volatile bool _hostRunning;
    private int _exiting;

    public TrayApplicationContext(string[] args)
    {
        _trayLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });
            logging.SetMinimumLevel(LogLevel.Information);
        });

        _logger = _trayLoggerFactory.CreateLogger<TrayApplicationContext>();
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _statusMenuItem = new ToolStripMenuItem("Status", null, (_, _) => ShowStatus());
        var openSpoolMenuItem = new ToolStripMenuItem("Open spool folder", null, (_, _) => OpenSpoolFolder());
        _pauseResumeMenuItem = new ToolStripMenuItem("Pause collection", null, (_, _) => ToggleCollectionPause())
        {
            Enabled = false
        };
        var exitMenuItem = new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync());

        _notifyIcon = new NotifyIcon
        {
            Text = "EndpointSignalAgent (starting)",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.AddRange([
            _statusMenuItem,
            openSpoolMenuItem,
            new ToolStripSeparator(),
            _pauseResumeMenuItem,
            new ToolStripSeparator(),
            exitMenuItem
        ]);

        _notifyIcon.DoubleClick += (_, _) => ShowStatus();

        _ = StartHostAsync(args);
    }

    private async Task StartHostAsync(string[] args)
    {
        try
        {
            _logger.LogInformation("Tray startup initiated");
            _host = AgentHostBootstrap.BuildHost(args);
            _host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(() =>
            {
                _hostRunning = false;
                _logger.LogInformation("Host stopped");
                _uiContext.Post(_ => UpdateTrayStatusText("EndpointSignalAgent (stopped)"), null);
            });

            await _host.StartAsync();
            _hostRunning = true;
            _collectionControl = _host.Services.GetRequiredService<ICollectionControl>();
            _pauseResumeMenuItem.Enabled = true;
            _logger.LogInformation("Host started");
            UpdatePauseMenuText();
            UpdateTrayStatusText("EndpointSignalAgent (running)");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Host startup failed");
            MessageBox.Show($"EndpointSignalAgent failed to start:\n\n{ex.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ExitAsync();
        }
    }

    private void ShowStatus()
    {
        var enrollment = ResolveEnrollmentState();
        var backend = ResolveBackendState();
        var paused = _collectionControl?.IsPaused == true;

        var text = new StringBuilder()
            .AppendLine($"Host running: {_hostRunning}")
            .AppendLine($"Collection paused: {paused}")
            .AppendLine($"Enrollment: {enrollment}")
            .AppendLine($"Backend: {backend}")
            .AppendLine("Spool files:")
            .AppendLine($"- {Path.GetFullPath(@"spool\signals.jsonl")}")
            .AppendLine($"- {Path.GetFullPath(@"spool\signals.offset")}")
            .AppendLine($"- {Path.GetFullPath(@"spool\features.db")}")
            .ToString();

        MessageBox.Show(text, "EndpointSignalAgent status", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private string ResolveBackendState()
    {
        if (_host is null)
        {
            return "unknown";
        }

        var options = _host.Services.GetRequiredService<IOptions<BackendOptions>>().Value;
        return options.UseBackend ? "enabled" : "disabled";
    }

    private string ResolveEnrollmentState()
    {
        try
        {
            var path = Path.Combine("spool", "enrollment.json");
            if (!File.Exists(path))
            {
                return "not enrolled yet";
            }

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("DeviceId", out var deviceIdElement))
            {
                var deviceId = deviceIdElement.GetString();
                return string.IsNullOrWhiteSpace(deviceId) ? "file present (invalid device id)" : $"enrolled ({deviceId})";
            }

            return "file present (missing device id)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read enrollment status");
            return "unknown (read error)";
        }
    }

    private void OpenSpoolFolder()
    {
        Directory.CreateDirectory("spool");
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetFullPath("spool"),
            UseShellExecute = true
        });
    }

    private void ToggleCollectionPause()
    {
        if (_collectionControl is null)
        {
            return;
        }

        if (_collectionControl.IsPaused)
        {
            _collectionControl.Resume();
            _logger.LogInformation("Signal collection resumed from tray menu");
        }
        else
        {
            _collectionControl.Pause();
            _logger.LogInformation("Signal collection paused from tray menu");
        }

        UpdatePauseMenuText();
    }

    private void UpdatePauseMenuText()
    {
        if (_collectionControl is null)
        {
            _pauseResumeMenuItem.Text = "Pause collection";
            return;
        }

        _pauseResumeMenuItem.Text = _collectionControl.IsPaused ? "Resume collection" : "Pause collection";
    }

    private void UpdateTrayStatusText(string text)
    {
        if (_notifyIcon.Icon is null)
        {
            return;
        }

        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            return;
        }

        _logger.LogInformation("Tray exit requested");

        _notifyIcon.Visible = false;

        if (_host is not null)
        {
            try
            {
                _logger.LogInformation("Host stopping");
                await _host.StopAsync(TimeSpan.FromSeconds(15));
                _logger.LogInformation("Host stopped cleanly");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping host");
            }
            finally
            {
                _host.Dispose();
                _host = null;
            }
        }

        _notifyIcon.Dispose();
        _trayLoggerFactory.Dispose();
        ExitThread();
    }
}
