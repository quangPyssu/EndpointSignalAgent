using EndpointSignalAgent.Bootstrap;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Services;
using EndpointSignalAgent.FeatureExtraction.Services;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace EndpointSignalAgent.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly (string Code, string Label)[] ScenarioCodes =
    [
        ("DIFFERENT_USER", "Different user"),
        ("UNUSUAL_APP_SEQUENCE", "Unusual app sequence"),
        ("UNUSUAL_TIME_CONTEXT", "Unusual time context"),
        ("NETWORK_CONTEXT_SHIFT", "Network context shift"),
        ("LOW_ACTIVITY_HIDE", "Low activity hide"),
        ("REMOTE_ACCESS_CONTEXT", "Remote access context"),
        ("SCRIPTED_SIMULATION_OTHER", "Scripted simulation other")
    ];

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _exportAllFeaturesMenuItem;
    private readonly ToolStripMenuItem _pauseResumeMenuItem;
    private readonly ILoggerFactory _trayLoggerFactory;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly SynchronizationContext _uiContext;

    private IHost? _host;
    private ICollectionControl? _collectionControl;
    private KeyboardCommandService? _keyboardCommandService;
    private ICollectionSessionService? _collectionSessionService;
    private IAbnormalTaggingService? _abnormalTaggingService;
    private IProgressTrackingService? _progressTrackingService;
    private DatasetExportService? _datasetExportService;
    private DatasetCollectionOptions? _datasetOptions;
    private AgentOptions? _agentOptions;
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
        _exportAllFeaturesMenuItem = new ToolStripMenuItem("Export all features to CSV", null, async (_, _) => await ExportAllFeaturesAsync())
        {
            Enabled = false
        };
        var openSpoolMenuItem = new ToolStripMenuItem("Open spool folder", null, (_, _) => OpenSpoolFolder());
        _pauseResumeMenuItem = new ToolStripMenuItem("Pause collection", null, (_, _) => ToggleCollectionPause())
        {
            Enabled = false
        };
        var startSessionMenuItem = new ToolStripMenuItem("Start collection session", null, async (_, _) => await StartSessionAsync()) { Enabled = false };
        var pauseResumeSessionMenuItem = new ToolStripMenuItem("Pause/resume collection session", null, async (_, _) => await PauseResumeSessionAsync()) { Enabled = false };
        var endSessionMenuItem = new ToolStripMenuItem("End collection session", null, async (_, _) => await EndSessionAsync()) { Enabled = false };
        var startAbnormalMenuItem = new ToolStripMenuItem("Start abnormal segment", null, async (_, _) => await StartAbnormalSegmentAsync()) { Enabled = false };
        var endAbnormalMenuItem = new ToolStripMenuItem("End abnormal segment", null, async (_, _) => await EndAbnormalSegmentAsync()) { Enabled = false };
        var markLast5MenuItem = new ToolStripMenuItem("Mark last 5 min abnormal", null, async (_, _) => await MarkLastFiveMinutesAsync()) { Enabled = false };
        var noteMenuItem = new ToolStripMenuItem("Enter short note", null, async (_, _) => await EnterShortNoteAsync()) { Enabled = false };
        var showProgressMenuItem = new ToolStripMenuItem("Show progress", null, async (_, _) => await ShowProgressAsync()) { Enabled = false };
        var exportDatasetMenuItem = new ToolStripMenuItem("Export dataset package", null, async (_, _) => await ExportDatasetPackageAsync()) { Enabled = false };
        var openManifestFolderMenuItem = new ToolStripMenuItem("Open manifest folder", null, (_, _) => OpenManifestFolder()) { Enabled = false };
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
            _exportAllFeaturesMenuItem,
            openSpoolMenuItem,
            openManifestFolderMenuItem,
            new ToolStripSeparator(),
            _pauseResumeMenuItem,
            startSessionMenuItem,
            pauseResumeSessionMenuItem,
            endSessionMenuItem,
            startAbnormalMenuItem,
            endAbnormalMenuItem,
            markLast5MenuItem,
            noteMenuItem,
            showProgressMenuItem,
            exportDatasetMenuItem,
            new ToolStripSeparator(),
            exitMenuItem
        ]);

        _notifyIcon.DoubleClick += (_, _) => ShowStatus();

        _ = StartHostAsync(args, startSessionMenuItem, pauseResumeSessionMenuItem, endSessionMenuItem, startAbnormalMenuItem, endAbnormalMenuItem, markLast5MenuItem, noteMenuItem, showProgressMenuItem, exportDatasetMenuItem, openManifestFolderMenuItem);
    }

    private async Task StartHostAsync(string[] args, params ToolStripMenuItem[] datasetMenuItems)
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
            _keyboardCommandService = _host.Services.GetService<KeyboardCommandService>();
            _collectionSessionService = _host.Services.GetService<ICollectionSessionService>();
            _abnormalTaggingService = _host.Services.GetService<IAbnormalTaggingService>();
            _progressTrackingService = _host.Services.GetService<IProgressTrackingService>();
            _datasetExportService = _host.Services.GetService<DatasetExportService>();
            _datasetOptions = _host.Services.GetService<IOptions<DatasetCollectionOptions>>()?.Value;
            _agentOptions = _host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

            _pauseResumeMenuItem.Enabled = true;
            _exportAllFeaturesMenuItem.Enabled = _keyboardCommandService is not null;
            var datasetMode = AgentModes.IsDatasetCollection(_agentOptions.Mode);
            foreach (var item in datasetMenuItems)
            {
                item.Enabled = datasetMode;
            }

            _logger.LogInformation("Host started");
            UpdatePauseMenuText();
            UpdateTrayStatusText($"EndpointSignalAgent ({_agentOptions.Mode})");
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
            .AppendLine($"Mode: {_agentOptions?.Mode ?? "unknown"}")
            .AppendLine($"Collection paused: {paused}")
            .AppendLine($"Enrollment: {enrollment}")
            .AppendLine($"Backend: {backend}")
            .AppendLine("Spool files:")
            .AppendLine($"- {Path.GetFullPath(@"spool\raw_signals.jsonl")}")
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

    private void OpenManifestFolder()
    {
        var path = _datasetOptions?.ManifestRoot ?? "spool/manifests";
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetFullPath(path),
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

    private async Task StartSessionAsync()
    {
        if (_collectionSessionService is null)
        {
            return;
        }

        var label = Prompt("Session Label", "Enter session label:", $"session-{DateTime.Now:yyyyMMdd-HHmmss}");
        var notes = Prompt("Session Notes", "Enter short note (optional):", "");
        var session = await _collectionSessionService.StartSessionAsync(label ?? string.Empty, normalOnly: false, notes, "tray", CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
        MessageBox.Show($"Session started:\n{session.SessionId}", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task PauseResumeSessionAsync()
    {
        if (_collectionSessionService is null)
        {
            return;
        }

        var current = _collectionSessionService.CurrentSession;
        if (current is null)
        {
            MessageBox.Show("No active session.", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(current.State, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            await _collectionSessionService.ResumeSessionAsync("tray", CancellationToken.None);
        }
        else
        {
            await _collectionSessionService.PauseSessionAsync("tray", CancellationToken.None);
        }

        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
    }

    private async Task EndSessionAsync()
    {
        if (_collectionSessionService is null)
        {
            return;
        }

        var notes = Prompt("End Session", "Optional end-session note:", "");
        var ended = await _collectionSessionService.EndSessionAsync(notes, "tray", CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);

        MessageBox.Show(ended is null ? "No active session." : $"Session ended: {ended.SessionId}", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task StartAbnormalSegmentAsync()
    {
        if (_abnormalTaggingService is null)
        {
            return;
        }

        var scenario = SelectScenarioCode();
        if (scenario is null)
        {
            return;
        }

        var notes = Prompt("Abnormal Segment", "Short note (optional):", "");
        await _abnormalTaggingService.StartAbnormalSegmentAsync(scenario.Value.Code, scenario.Value.Label, "tray", 0.9, notes, CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
    }

    private async Task EndAbnormalSegmentAsync()
    {
        if (_abnormalTaggingService is null)
        {
            return;
        }

        var notes = Prompt("End abnormal", "Short note (optional):", "");
        await _abnormalTaggingService.EndAbnormalSegmentAsync("tray", notes, CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
    }

    private async Task MarkLastFiveMinutesAsync()
    {
        if (_abnormalTaggingService is null)
        {
            return;
        }

        var scenario = SelectScenarioCode();
        if (scenario is null)
        {
            return;
        }

        var notes = Prompt("Mark last 5 minutes", "Short note (optional):", "");
        await _abnormalTaggingService.MarkLastMinutesAbnormalAsync(5, scenario.Value.Code, scenario.Value.Label, "tray", 0.75, notes, CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
    }

    private async Task EnterShortNoteAsync()
    {
        var note = Prompt("Dataset Note", "Enter short note:", "");
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        if (_collectionSessionService is not null && _collectionSessionService.CurrentSession is not null)
        {
            await _collectionSessionService.PauseSessionAsync("tray-note", CancellationToken.None);
            await _collectionSessionService.ResumeSessionAsync("tray-note", CancellationToken.None);
        }

        MessageBox.Show("Note captured in latest session transitions.", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task ShowProgressAsync()
    {
        if (_progressTrackingService is null)
        {
            return;
        }

        var progress = await _progressTrackingService.GetCurrentAsync(CancellationToken.None);
        var text = new StringBuilder()
            .AppendLine($"Study span (weeks): {progress.StudySpanWeeks}")
            .AppendLine($"Valid collection days: {progress.ValidCollectionDays}")
            .AppendLine($"Sessions completed: {progress.TotalSessionsCompleted}")
            .AppendLine($"Runtime hours: {progress.TotalRuntimeHours:F2}")
            .AppendLine($"Active hours: {progress.TotalActiveHours:F2}")
            .AppendLine($"Abnormal scenarios completed: {progress.AbnormalScenariosCompleted}")
            .AppendLine($"Abnormal minutes: {progress.AbnormalMinutes:F1}")
            .AppendLine($"Coverage days ok: {progress.CoreSignalCoverageDaysOk}")
            .AppendLine($"Completion status: {progress.CompletionStatus}")
            .AppendLine($"Completion ratio: {progress.CompletionRatio:P1}")
            .AppendLine($"Last updated UTC: {progress.LastUpdatedUtc:O}")
            .ToString();

        MessageBox.Show(text, "Dataset progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task ExportDatasetPackageAsync()
    {
        if (_datasetExportService is null || _datasetOptions is null)
        {
            return;
        }

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var folder = await _datasetExportService.ExportParticipantPackageAsync(_datasetOptions.ParticipantId, version, CancellationToken.None);
        MessageBox.Show($"Dataset package exported:\n{Path.GetFullPath(folder)}", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task ExportAllFeaturesAsync()
    {
        if (_keyboardCommandService is null)
        {
            MessageBox.Show("Feature export service is not available.", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _logger.LogInformation("Tray menu requested full feature export (equivalent to Ctrl+O)");
            var result = await _keyboardCommandService.ExportAllFeatureDataAsync(CancellationToken.None);

            if (!result.Success)
            {
                MessageBox.Show($"Feature export failed: {result.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var detail = result.FilePath is null
                ? result.Message
                : $"{result.Message}\n\nFile:\n{result.FilePath}";

            MessageBox.Show(detail, "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray export-all-features action failed");
            MessageBox.Show($"Feature export failed: {ex.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private (string Code, string Label)? SelectScenarioCode()
    {
        var selection = Prompt("Scenario Code", "Enter scenario code:\n" + string.Join("\n", ScenarioCodes.Select(s => $"- {s.Code}")), ScenarioCodes[0].Code);
        if (string.IsNullOrWhiteSpace(selection))
        {
            return null;
        }

        var match = ScenarioCodes.FirstOrDefault(s => string.Equals(s.Code, selection.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match.Code) ? (selection.Trim().ToUpperInvariant(), selection.Trim()) : match;
    }

    private static string? Prompt(string title, string message, string defaultValue)
    {
        using var form = new Form
        {
            Width = 480,
            Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false
        };

        var textLabel = new Label { Left = 12, Top = 12, Width = 440, Text = message };
        var textBox = new TextBox { Left = 12, Top = 40, Width = 440, Text = defaultValue };
        var confirmation = new Button { Text = "OK", Left = 290, Width = 75, Top = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 377, Width = 75, Top = 80, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([textLabel, textBox, confirmation, cancel]);
        form.AcceptButton = confirmation;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
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
