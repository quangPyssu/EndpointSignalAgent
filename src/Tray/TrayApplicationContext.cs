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
    private readonly ToolStripMenuItem _extractRawSignalsToDbMenuItem;
    private readonly ToolStripMenuItem _clearFeatureDbMenuItem;
    private readonly ToolStripMenuItem _pauseResumeMenuItem;
    private readonly ToolStripMenuItem _startSessionMenuItem;
    private readonly ToolStripMenuItem _pauseSessionMenuItem;
    private readonly ToolStripMenuItem _resumeSessionMenuItem;
    private readonly ToolStripMenuItem _endSessionMenuItem;
    private readonly ToolStripMenuItem _startAbnormalMenuItem;
    private readonly ToolStripMenuItem _endAbnormalMenuItem;
    private readonly ToolStripMenuItem _markLast5MenuItem;
    private readonly ToolStripMenuItem _noteMenuItem;
    private readonly ToolStripMenuItem _openManifestFolderMenuItem;
    private readonly ToolStripMenuItem _exportDatasetMenuItem;
    private readonly ToolStripMenuItem _showProgressDetailsMenuItem;
    private readonly ToolStripMenuItem _progressMenuItem;
    private readonly ToolStripMenuItem _completionMenuItem;
    private readonly ToolStripMenuItem _statusProgressMenuItem;
    private readonly ToolStripMenuItem _totalCollectedMenuItem;
    private readonly ToolStripMenuItem _activeTimeMenuItem;
    private readonly ToolStripMenuItem _sessionsMenuItem;
    private readonly ToolStripMenuItem _daysMenuItem;
    private readonly ProgressBar _progressBar;
    private readonly Label _progressPercentLabel;
    private readonly ToolStripControlHost _progressHost;
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
    private IDatasetShutdownCoordinator? _datasetShutdownCoordinator;
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
        _exportAllFeaturesMenuItem = new ToolStripMenuItem("Export all features to CSV", null, async (_, _) => await ExportAllFeaturesAsync()) { Enabled = false };
        _extractRawSignalsToDbMenuItem = new ToolStripMenuItem("Translate raw_signals.jsonl to DB", null, async (_, _) => await ExtractRawSignalsToDbAsync()) { Enabled = false };
        _clearFeatureDbMenuItem = new ToolStripMenuItem("Clear feature database", null, async (_, _) => await ClearFeatureDatabaseAsync()) { Enabled = false };
        _pauseResumeMenuItem = new ToolStripMenuItem("Pause collection", null, (_, _) => ToggleCollectionPause()) { Enabled = false };
        _startSessionMenuItem = new ToolStripMenuItem("Start session", null, async (_, _) => await StartSessionAsync()) { Enabled = false };
        _pauseSessionMenuItem = new ToolStripMenuItem("Pause session", null, async (_, _) => await PauseSessionAsync()) { Enabled = false };
        _resumeSessionMenuItem = new ToolStripMenuItem("Resume session", null, async (_, _) => await ResumeSessionAsync()) { Enabled = false };
        _endSessionMenuItem = new ToolStripMenuItem("End session", null, async (_, _) => await EndSessionAsync()) { Enabled = false };
        _startAbnormalMenuItem = new ToolStripMenuItem("Start abnormal segment", null, async (_, _) => await StartAbnormalSegmentAsync()) { Enabled = false };
        _endAbnormalMenuItem = new ToolStripMenuItem("End abnormal segment", null, async (_, _) => await EndAbnormalSegmentAsync()) { Enabled = false };
        _markLast5MenuItem = new ToolStripMenuItem("Mark last 5 min abnormal", null, async (_, _) => await MarkLastFiveMinutesAsync()) { Enabled = false };
        _noteMenuItem = new ToolStripMenuItem("Enter short note", null, async (_, _) => await EnterShortNoteAsync()) { Enabled = false };
        _openManifestFolderMenuItem = new ToolStripMenuItem("Open manifest folder", null, (_, _) => OpenManifestFolder()) { Enabled = false };
        _exportDatasetMenuItem = new ToolStripMenuItem("Export dataset package", null, async (_, _) => await ExportDatasetPackageAsync()) { Enabled = false };
        _showProgressDetailsMenuItem = new ToolStripMenuItem("Open progress details...", null, async (_, _) => await ShowProgressAsync()) { Enabled = false };

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1000, Value = 0, Size = new Size(140, 18), Location = new Point(0, 2) };
        _progressPercentLabel = new Label { AutoSize = true, Location = new Point(148, 3), Text = "0%" };
        var progressPanel = new Panel { Width = 220, Height = 24 };
        progressPanel.Controls.Add(_progressBar);
        progressPanel.Controls.Add(_progressPercentLabel);
        _progressHost = new ToolStripControlHost(progressPanel) { AutoSize = false, Size = new Size(230, 28), Enabled = false };

        _completionMenuItem = new ToolStripMenuItem("Completion: 0%") { Enabled = false };
        _statusProgressMenuItem = new ToolStripMenuItem("Status: Unknown") { Enabled = false };
        _totalCollectedMenuItem = new ToolStripMenuItem("Total collected: 0h 0m") { Enabled = false };
        _activeTimeMenuItem = new ToolStripMenuItem("Active time: 0h 0m") { Enabled = false };
        _sessionsMenuItem = new ToolStripMenuItem("Sessions: 0") { Enabled = false };
        _daysMenuItem = new ToolStripMenuItem("Days: 0") { Enabled = false };

        _progressMenuItem = new ToolStripMenuItem("Progress") { Enabled = false };
        _progressMenuItem.DropDownItems.AddRange([
            _progressHost,
            _completionMenuItem,
            _statusProgressMenuItem,
            _totalCollectedMenuItem,
            _activeTimeMenuItem,
            _sessionsMenuItem,
            _daysMenuItem,
            new ToolStripSeparator(),
            _showProgressDetailsMenuItem
        ]);
        _progressMenuItem.DropDownOpening += async (_, _) => await RefreshProgressMenuAsync();

        var collectionMenuItem = new ToolStripMenuItem("Collection");
        collectionMenuItem.DropDownItems.AddRange([
            _pauseResumeMenuItem,
            new ToolStripMenuItem("Open spool folder", null, (_, _) => OpenSpoolFolder()),
            _openManifestFolderMenuItem
        ]);

        var sessionMenuItem = new ToolStripMenuItem("Session");
        sessionMenuItem.DropDownItems.AddRange([
            _startSessionMenuItem,
            _pauseSessionMenuItem,
            _resumeSessionMenuItem,
            _endSessionMenuItem,
            _noteMenuItem
        ]);

        var abnormalMenuItem = new ToolStripMenuItem("Abnormal");
        abnormalMenuItem.DropDownItems.AddRange([
            _startAbnormalMenuItem,
            _endAbnormalMenuItem,
            _markLast5MenuItem
        ]);

        var exportMenuItem = new ToolStripMenuItem("Export");
        exportMenuItem.DropDownItems.AddRange([
            _exportDatasetMenuItem
        ]);

        var databaseMenuItem = new ToolStripMenuItem("Database");
        databaseMenuItem.DropDownItems.AddRange([
            _extractRawSignalsToDbMenuItem,
            _exportAllFeaturesMenuItem,
            _clearFeatureDbMenuItem
        ]);

        var exitMenuItem = new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync("tray_exit"));

        _notifyIcon = new NotifyIcon
        {
            Text = "EndpointSignalAgent (starting)",
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "icon.ico")),
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Opening += async (_, _) => await RefreshMenuStateAsync();
        _notifyIcon.ContextMenuStrip.Items.AddRange([
            _statusMenuItem,
            new ToolStripSeparator(),
            collectionMenuItem,
            sessionMenuItem,
            abnormalMenuItem,
            _progressMenuItem,
            exportMenuItem,
            databaseMenuItem,
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
            _keyboardCommandService = _host.Services.GetService<KeyboardCommandService>();
            _collectionSessionService = _host.Services.GetService<ICollectionSessionService>();
            _abnormalTaggingService = _host.Services.GetService<IAbnormalTaggingService>();
            _progressTrackingService = _host.Services.GetService<IProgressTrackingService>();
            _datasetExportService = _host.Services.GetService<DatasetExportService>();
            _datasetShutdownCoordinator = _host.Services.GetService<IDatasetShutdownCoordinator>();
            _datasetOptions = _host.Services.GetService<IOptions<DatasetCollectionOptions>>()?.Value;
            _agentOptions = _host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

            _pauseResumeMenuItem.Enabled = true;
            var hasKeyboardService = _keyboardCommandService is not null;
            _exportAllFeaturesMenuItem.Enabled = hasKeyboardService;
            _extractRawSignalsToDbMenuItem.Enabled = hasKeyboardService;
            _clearFeatureDbMenuItem.Enabled = hasKeyboardService;
            var datasetMode = AgentModes.IsDatasetCollection(_agentOptions.Mode);
            _progressMenuItem.Enabled = datasetMode;
            _showProgressDetailsMenuItem.Enabled = datasetMode;
            _exportDatasetMenuItem.Enabled = datasetMode;
            _openManifestFolderMenuItem.Enabled = datasetMode;

            _logger.LogInformation("Host started");
            UpdatePauseMenuText();
            await RefreshMenuStateAsync();
            await UpdateDatasetSessionStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Host startup failed");
            MessageBox.Show($"EndpointSignalAgent failed to start:\n\n{ex.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ExitAsync("startup_error");
        }
    }

    private async Task RefreshMenuStateAsync()
    {
        UpdatePauseMenuText();

        var datasetMode = AgentModes.IsDatasetCollection(_agentOptions?.Mode ?? string.Empty);
        if (!datasetMode || _collectionSessionService is null || _abnormalTaggingService is null)
        {
            _startSessionMenuItem.Enabled = false;
            _pauseSessionMenuItem.Enabled = false;
            _resumeSessionMenuItem.Enabled = false;
            _endSessionMenuItem.Enabled = false;
            _startAbnormalMenuItem.Enabled = false;
            _endAbnormalMenuItem.Enabled = false;
            _markLast5MenuItem.Enabled = false;
            _noteMenuItem.Enabled = false;
            return;
        }

        var current = _collectionSessionService.CurrentSession;
        var hasActiveSession = current is { State: "Running" or "Paused" };
        var isRunning = current is { State: "Running" };
        var isPaused = current is { State: "Paused" };
        var activeAbnormal = await _abnormalTaggingService.GetActiveAnnotationAsync(CancellationToken.None);

        _startSessionMenuItem.Enabled = !hasActiveSession;
        _pauseSessionMenuItem.Enabled = isRunning;
        _resumeSessionMenuItem.Enabled = isPaused;
        _endSessionMenuItem.Enabled = isRunning || isPaused;

        _startAbnormalMenuItem.Enabled = hasActiveSession && activeAbnormal is null;
        _endAbnormalMenuItem.Enabled = activeAbnormal is not null;
        _markLast5MenuItem.Enabled = hasActiveSession;
        _noteMenuItem.Enabled = hasActiveSession;
    }

    private async Task RefreshProgressMenuAsync()
    {
        if (_progressTrackingService is null)
        {
            return;
        }

        var snapshot = await _progressTrackingService.GetTraySnapshotAsync(CancellationToken.None);
        var ratio = Math.Clamp(snapshot.CompletionRatio, 0, 1);
        var percent = ratio * 100;
        _progressBar.Value = Math.Max(_progressBar.Minimum, Math.Min(_progressBar.Maximum, (int)Math.Round(ratio * _progressBar.Maximum)));
        _progressPercentLabel.Text = $"{percent:0.#}%";
        _completionMenuItem.Text = $"Completion: {percent:0.#}%";
        _statusProgressMenuItem.Text = $"Status: {snapshot.CompletionStatus}";
        _totalCollectedMenuItem.Text = $"Total collected: {FormatDuration(snapshot.TotalRuntimeHours)}";
        _activeTimeMenuItem.Text = $"Active time: {FormatDuration(snapshot.TotalActiveHours)}";
        _sessionsMenuItem.Text = $"Sessions: {snapshot.TotalSessionsCompleted}";
        _daysMenuItem.Text = $"Days: {snapshot.ValidCollectionDays}";
    }

    private static string FormatDuration(double hours)
    {
        var totalMinutes = (int)Math.Max(0, Math.Round(hours * 60));
        var days = totalMinutes / (24 * 60);
        var remainder = totalMinutes % (24 * 60);
        var h = remainder / 60;
        var m = remainder % 60;
        return days > 0 ? $"{days}d {h}h {m}m" : $"{h}h {m}m";
    }

    private void ShowStatus()
    {
        var enrollment = ResolveEnrollmentState();
        var backend = ResolveBackendState();
        var paused = _collectionControl?.IsPaused == true;

        var text = new StringBuilder()
            .AppendLine($"Host running: {_hostRunning}")
            .AppendLine($"Mode: {_agentOptions?.Mode ?? "unknown"}")
            .AppendLine($"Dataset session active: {(_collectionSessionService?.CurrentSession is not null)}")
            .AppendLine($"Dataset session state: {_collectionSessionService?.CurrentSession?.State ?? "none"}")
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

        if (_collectionSessionService.CurrentSession is { State: "Running" or "Paused" } current)
        {
            MessageBox.Show($"Session already active:\n{current.SessionId}", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await UpdateDatasetSessionStatusAsync();
            return;
        }

        var label = Prompt("Session Label", "Enter session label:", $"session-{DateTime.Now:yyyyMMdd-HHmmss}");
        var notes = Prompt("Session Notes", "Enter short note (optional):", "");
        var session = await _collectionSessionService.StartSessionAsync(label ?? string.Empty, normalOnly: false, notes, "tray", CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
        await UpdateDatasetSessionStatusAsync();
        MessageBox.Show($"Session started:\n{session.SessionId}", "Dataset collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task PauseSessionAsync()
    {
        if (_collectionSessionService is null)
        {
            return;
        }

        await _collectionSessionService.PauseSessionAsync("tray", CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
        await UpdateDatasetSessionStatusAsync();
    }

    private async Task ResumeSessionAsync()
    {
        if (_collectionSessionService is null)
        {
            return;
        }

        await _collectionSessionService.ResumeSessionAsync("tray", CancellationToken.None);
        await _progressTrackingService!.RecalculateAsync(CancellationToken.None);
        await UpdateDatasetSessionStatusAsync();
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
        await UpdateDatasetSessionStatusAsync();

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

            var detail = result.FilePaths.Count == 0
                ? result.Message
                : $"{result.Message}\n\nFiles:\n{string.Join("\n", result.FilePaths)}";

            MessageBox.Show(detail, "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray export-all-features action failed");
            MessageBox.Show($"Feature export failed: {ex.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    private async Task ExtractRawSignalsToDbAsync()
    {
        if (_keyboardCommandService is null)
        {
            MessageBox.Show("Feature extraction service is not available.", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _logger.LogInformation("Tray menu requested raw_signals.jsonl translation into feature DB");
            await _keyboardCommandService.ExtractFeaturesFromAllSignalsAsync(CancellationToken.None);
            MessageBox.Show("Raw signals translation finished. Feature rows were written to the local database.", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray raw-to-db action failed");
            MessageBox.Show($"Raw signal translation failed: {ex.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ClearFeatureDatabaseAsync()
    {
        if (_keyboardCommandService is null)
        {
            MessageBox.Show("Feature database service is not available.", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show("Delete all feature rows from the local database?", "EndpointSignalAgent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var deleted = await _keyboardCommandService.ClearDatabaseAsync(CancellationToken.None);
            MessageBox.Show($"Deleted {deleted} feature rows.", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray clear-db action failed");
            MessageBox.Show($"Clear database failed: {ex.Message}", "EndpointSignalAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private Task UpdateDatasetSessionStatusAsync()
    {
        var mode = _agentOptions?.Mode ?? "unknown";
        if (!AgentModes.IsDatasetCollection(mode))
        {
            UpdateTrayStatusText($"EndpointSignalAgent ({mode})");
            return Task.CompletedTask;
        }

        var current = _collectionSessionService?.CurrentSession;
        var sessionState = current is null ? "inactive" : $"{current.State.ToLowerInvariant()}";
        UpdateTrayStatusText($"EndpointSignalAgent ({mode}, session:{sessionState})");
        return Task.CompletedTask;
    }

    private async Task ExitAsync(string reason)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            return;
        }

        _logger.LogInformation("Tray exit requested");
        _notifyIcon.Visible = false;

        if (_datasetShutdownCoordinator is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _datasetShutdownCoordinator.FinalizeAsync(reason, cts.Token);
        }

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
