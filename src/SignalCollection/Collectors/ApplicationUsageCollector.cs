using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Utilities;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace EndpointSignalAgent.SignalCollection.Collectors;

public sealed class ApplicationUsageCollector : SignalCollectorBase
{
    private readonly ILogger<ApplicationUsageCollector> _logger;
    private readonly IForegroundSource _foregroundSource;
    private readonly IProcessInfoResolver _processResolver;
    private readonly IClock _clock;
    private readonly EndpointSignalAgent.SignalCollection.Collectors.Network.IHashingService _hashing;

    private readonly TimeSpan _fallbackPollInterval = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _switchRateTickInterval = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _debouncePollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Channel<InputMessage> _input = Channel.CreateUnbounded<InputMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private volatile bool _needsDebouncePolling;

    public ApplicationUsageCollector(
        ILogger<ApplicationUsageCollector> logger,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl)
        : this(
            logger,
            broadcaster,
            collectionControl,
            new WindowsForegroundSource(),
            new WindowsProcessInfoResolver(),
            new SystemClock(),
            new EndpointSignalAgent.SignalCollection.Collectors.Network.HashingService(
                new EndpointSignalAgent.SignalCollection.Collectors.Network.StableSaltProvider()))
    {
    }

    internal ApplicationUsageCollector(
        ILogger<ApplicationUsageCollector> logger,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl,
        IForegroundSource foregroundSource,
        IProcessInfoResolver processResolver,
        IClock clock,
        EndpointSignalAgent.SignalCollection.Collectors.Network.IHashingService hashing)
        : base(@"spool\signals.jsonl", broadcaster, collectionControl)
    {
        _logger = logger;
        _foregroundSource = foregroundSource;
        _processResolver = processResolver;
        _clock = clock;
        _hashing = hashing;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ApplicationUsageCollector started.");

        var hookActive = _foregroundSource.Start(
            sample => _input.Writer.TryWrite(InputMessage.FromObservation(sample)),
            _logger);

        var emitter = new BroadcasterEmitter(this);
        var stateMachine = new ApplicationUsageStateMachine(
            emitter,
            _processResolver,
            _clock,
            _hashing,
            () => _needsDebouncePolling = true,
            () => _needsDebouncePolling = false,
            hookActive ? "hook" : "poll");

        _input.Writer.TryWrite(InputMessage.FromObservation(_foregroundSource.Poll(_clock.UtcNow, "poll")));

        var fallbackPollTask = RunFallbackPollingAsync(stoppingToken);
        var debouncePollTask = RunDebouncePollingAsync(stoppingToken);
        var switchRateTickTask = RunSwitchRateTickAsync(stoppingToken);

        try
        {
            await foreach (var msg in _input.Reader.ReadAllAsync(stoppingToken))
            {
                if (msg.Kind == InputKind.TimerTick)
                {
                    await stateMachine.HandleTimerTickAsync(msg.TimestampUtc);
                    continue;
                }

                await stateMachine.HandleObservationAsync(msg.Observation);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApplicationUsageCollector loop error.");
        }
        finally
        {
            _input.Writer.TryComplete();
            await Task.WhenAll(fallbackPollTask, debouncePollTask, switchRateTickTask);
            await stateMachine.FlushShutdownAsync(_clock.UtcNow);
            _foregroundSource.Dispose();
        }

        _logger.LogInformation("ApplicationUsageCollector stopped.");
    }

    private async Task RunFallbackPollingAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_fallbackPollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _input.Writer.TryWrite(InputMessage.FromObservation(_foregroundSource.Poll(_clock.UtcNow, "poll")));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task RunDebouncePollingAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_debouncePollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_needsDebouncePolling)
                {
                    continue;
                }

                _input.Writer.TryWrite(InputMessage.FromObservation(_foregroundSource.Poll(_clock.UtcNow, "poll")));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task RunSwitchRateTickAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_switchRateTickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _input.Writer.TryWrite(InputMessage.FromTick(_clock.UtcNow));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private sealed class BroadcasterEmitter : IApplicationUsageEmitter
    {
        private readonly ApplicationUsageCollector _owner;

        public BroadcasterEmitter(ApplicationUsageCollector owner)
        {
            _owner = owner;
        }

        public Task EmitAsync(SignalEventType type, Dictionary<string, string> payload)
        {
            return _owner.WriteSignalAsync(type, payload);
        }
    }

    private readonly record struct InputMessage(InputKind Kind, ForegroundSample Observation, DateTimeOffset TimestampUtc)
    {
        public static InputMessage FromObservation(ForegroundSample observation) => new(InputKind.ForegroundObservation, observation, observation.ObservedAtUtc);
        public static InputMessage FromTick(DateTimeOffset nowUtc) => new(InputKind.TimerTick, default, nowUtc);
    }

    private enum InputKind
    {
        ForegroundObservation = 0,
        TimerTick = 1
    }
}

internal interface IForegroundSource : IDisposable
{
    bool Start(Action<ForegroundSample> onForegroundChanged, ILogger logger);
    ForegroundSample Poll(DateTimeOffset observedAtUtc, string collectorMode);
}

internal interface IProcessInfoResolver
{
    bool TryResolve(uint processId, out ProcessResolution resolution);
}

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal readonly record struct ForegroundSample(bool HasForeground, uint ProcessId, DateTimeOffset ObservedAtUtc, string CollectorMode)
{
    public static ForegroundSample Active(uint processId, DateTimeOffset observedAtUtc, string collectorMode) =>
        new(true, processId, observedAtUtc, collectorMode);

    public static ForegroundSample Inactive(DateTimeOffset observedAtUtc, string collectorMode) =>
        new(false, 0, observedAtUtc, collectorMode);
}

internal readonly record struct ProcessResolution(string ExeName, string HashInput, bool HasFullPath);

internal interface IApplicationUsageEmitter
{
    Task EmitAsync(SignalEventType type, Dictionary<string, string> payload);
}

internal sealed class ApplicationUsageStateMachine
{
    private readonly IApplicationUsageEmitter _emitter;
    private readonly IProcessInfoResolver _processResolver;
    private readonly EndpointSignalAgent.SignalCollection.Collectors.Network.IHashingService _hashing;
    private readonly Action _onNeedsDebouncePolling;
    private readonly Action _onStopDebouncePolling;
    private readonly string _defaultCollectorMode;

    private readonly TimeSpan _switchRateWindow = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _inactiveCloseThreshold = TimeSpan.FromSeconds(1.5);
    private readonly TimeSpan _debounceThreshold = TimeSpan.FromMilliseconds(400);

    private CurrentSlice? _current;
    private PendingCandidate? _pending;

    private bool _inactiveOpen;
    private bool _inactiveClosedCurrent;
    private DateTimeOffset _inactiveSince;
    private int _inactivePolls;

    private int _switchesInWindow;
    private DateTimeOffset _windowStart;

    public ApplicationUsageStateMachine(
        IApplicationUsageEmitter emitter,
        IProcessInfoResolver processResolver,
        IClock clock,
        EndpointSignalAgent.SignalCollection.Collectors.Network.IHashingService hashing,
        Action onNeedsDebouncePolling,
        Action onStopDebouncePolling,
        string defaultCollectorMode)
    {
        _emitter = emitter;
        _processResolver = processResolver;
        _hashing = hashing;
        _onNeedsDebouncePolling = onNeedsDebouncePolling;
        _onStopDebouncePolling = onStopDebouncePolling;
        _defaultCollectorMode = defaultCollectorMode;
        _windowStart = clock.UtcNow;
    }

    public async Task HandleTimerTickAsync(DateTimeOffset nowUtc)
    {
        await EmitSwitchRateIfWindowElapsedAsync(nowUtc);
    }

    public async Task HandleObservationAsync(ForegroundSample sample)
    {
        var now = sample.ObservedAtUtc;
        await EmitSwitchRateIfWindowElapsedAsync(now);

        if (!TryResolveForeground(sample, out var app))
        {
            await HandleInactiveAsync(sample);
            return;
        }

        ResetInactivity();
        await HandleActiveAsync(sample, app);
    }

    public async Task FlushShutdownAsync(DateTimeOffset nowUtc)
    {
        await EmitSwitchRateIfWindowElapsedAsync(nowUtc);

        if (_current is null)
        {
            return;
        }

        await EmitDwellAsync(_current.Value.App, _current.Value.SinceUtc, nowUtc, "shutdown_flush", _defaultCollectorMode);
        _current = null;
    }

    private bool TryResolveForeground(ForegroundSample sample, out ForegroundApp app)
    {
        app = default;
        if (!sample.HasForeground || sample.ProcessId == 0)
        {
            return false;
        }

        if (!_processResolver.TryResolve(sample.ProcessId, out var resolution))
        {
            return false;
        }

        var appKey = _hashing.HashStable($"app|{resolution.HashInput}");
        var category = ApplicationCategorizer.Categorize(resolution.ExeName);
        var confidence = resolution.HasFullPath ? "high" : "low";
        app = new ForegroundApp(appKey, category, confidence);
        return true;
    }

    private async Task HandleInactiveAsync(ForegroundSample sample)
    {
        CancelPending();

        if (!_inactiveOpen)
        {
            _inactiveOpen = true;
            _inactiveClosedCurrent = false;
            _inactiveSince = sample.ObservedAtUtc;
            _inactivePolls = 1;
            return;
        }

        _inactivePolls++;
        if (_current is null || _inactiveClosedCurrent)
        {
            return;
        }

        var shouldClose = _inactivePolls >= 2 || (sample.ObservedAtUtc - _inactiveSince) >= _inactiveCloseThreshold;
        if (!shouldClose)
        {
            return;
        }

        await EmitDwellAsync(_current.Value.App, _current.Value.SinceUtc, _inactiveSince, "no_foreground", sample.CollectorMode);
        _current = null;
        _inactiveClosedCurrent = true;
    }

    private async Task HandleActiveAsync(ForegroundSample sample, ForegroundApp app)
    {
        if (_current is null && _pending is null)
        {
            StartPending(app, sample.ObservedAtUtc, sample.CollectorMode);
            await TryCommitPendingAsync(sample.ObservedAtUtc);
            return;
        }

        if (_pending is not null)
        {
            if (AppEquals(_pending.Value.App, app))
            {
                _pending = _pending.Value with { Confirmations = _pending.Value.Confirmations + 1 };
                await TryCommitPendingAsync(sample.ObservedAtUtc);
                return;
            }

            if (_current is not null && AppEquals(_current.Value.App, app))
            {
                CancelPending();
                return;
            }

            StartPending(app, sample.ObservedAtUtc, sample.CollectorMode);
            await TryCommitPendingAsync(sample.ObservedAtUtc);
            return;
        }

        if (_current is not null && AppEquals(_current.Value.App, app))
        {
            return;
        }

        StartPending(app, sample.ObservedAtUtc, sample.CollectorMode);
        await TryCommitPendingAsync(sample.ObservedAtUtc);
    }

    private void StartPending(ForegroundApp app, DateTimeOffset firstSeenUtc, string collectorMode)
    {
        _pending = new PendingCandidate(app, firstSeenUtc, 1, collectorMode);
        _onNeedsDebouncePolling();
    }

    private async Task TryCommitPendingAsync(DateTimeOffset nowUtc)
    {
        if (_pending is null)
        {
            return;
        }

        var pending = _pending.Value;
        var elapsed = nowUtc - pending.FirstSeenUtc;
        var commit = pending.Confirmations >= 2 || elapsed >= _debounceThreshold;
        if (!commit)
        {
            return;
        }

        if (_current is not null && !AppEquals(_current.Value.App, pending.App))
        {
            await EmitDwellAsync(_current.Value.App, _current.Value.SinceUtc, pending.FirstSeenUtc, "switch", pending.CollectorMode);
            _switchesInWindow++;
        }

        _current = new CurrentSlice(pending.App, pending.FirstSeenUtc);
        _pending = null;
        _onStopDebouncePolling();

        await _emitter.EmitAsync(SignalEventType.ForegroundAppChanged, new Dictionary<string, string>
        {
            ["appKey"] = _current.Value.App.AppKey,
            ["category"] = _current.Value.App.Category,
            ["collectorMode"] = pending.CollectorMode,
            ["confidence"] = _current.Value.App.Confidence
        });
    }

    private void CancelPending()
    {
        if (_pending is null)
        {
            return;
        }

        _pending = null;
        _onStopDebouncePolling();
    }

    private void ResetInactivity()
    {
        _inactiveOpen = false;
        _inactiveClosedCurrent = false;
        _inactivePolls = 0;
    }

    private async Task EmitDwellAsync(ForegroundApp app, DateTimeOffset fromUtc, DateTimeOffset toUtc, string reason, string collectorMode)
    {
        var durationMs = (long)(toUtc - fromUtc).TotalMilliseconds;
        if (durationMs < 0)
        {
            durationMs = 0;
        }

        await _emitter.EmitAsync(SignalEventType.AppDwell, new Dictionary<string, string>
        {
            ["appKey"] = app.AppKey,
            ["category"] = app.Category,
            ["durationMs"] = durationMs.ToString(),
            ["reason"] = reason,
            ["dwellReason"] = reason,
            ["collectorMode"] = collectorMode,
            ["confidence"] = app.Confidence
        });
    }

    private async Task EmitSwitchRateIfWindowElapsedAsync(DateTimeOffset nowUtc)
    {
        if (nowUtc - _windowStart < _switchRateWindow)
        {
            return;
        }

        await _emitter.EmitAsync(SignalEventType.AppSwitchRate, new Dictionary<string, string>
        {
            ["windowSec"] = ((int)_switchRateWindow.TotalSeconds).ToString(),
            ["switches"] = _switchesInWindow.ToString(),
            ["collectorMode"] = _defaultCollectorMode
        });

        _switchesInWindow = 0;
        _windowStart = nowUtc;
    }

    private static bool AppEquals(ForegroundApp left, ForegroundApp right)
    {
        return string.Equals(left.AppKey, right.AppKey, StringComparison.Ordinal);
    }

    private readonly record struct ForegroundApp(string AppKey, string Category, string Confidence);
    private readonly record struct CurrentSlice(ForegroundApp App, DateTimeOffset SinceUtc);
    private readonly record struct PendingCandidate(ForegroundApp App, DateTimeOffset FirstSeenUtc, int Confirmations, string CollectorMode);
}

internal sealed class WindowsProcessInfoResolver : IProcessInfoResolver
{
    public bool TryResolve(uint processId, out ProcessResolution resolution)
    {
        resolution = default;
        if (processId == 0)
        {
            return false;
        }

        if (TryResolveWithQueryFullProcessImageName(processId, out resolution))
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName?.Trim();
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            resolution = new ProcessResolution(processName, processName, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveWithQueryFullProcessImageName(uint processId, out ProcessResolution resolution)
    {
        resolution = default;
        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }

            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(processHandle, 0, buffer, ref size))
            {
                return false;
            }

            var fullPath = buffer.ToString().Trim();
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            var exeName = Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(exeName))
            {
                exeName = Path.GetFileName(fullPath);
            }

            if (string.IsNullOrWhiteSpace(exeName))
            {
                return false;
            }

            resolution = new ProcessResolution(exeName, fullPath, true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

internal sealed class WindowsForegroundSource : IForegroundSource
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private IntPtr _hookHandle;
    private WinEventDelegate? _callback;
    private Action<ForegroundSample>? _onForegroundChanged;

    public bool Start(Action<ForegroundSample> onForegroundChanged, ILogger logger)
    {
        _onForegroundChanged = onForegroundChanged;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        _callback = HandleWinEvent;
        _hookHandle = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        if (_hookHandle == IntPtr.Zero)
        {
            logger.LogWarning("Foreground hook unavailable; collector will use polling fallback.");
            return false;
        }

        return true;
    }

    public ForegroundSample Poll(DateTimeOffset observedAtUtc, string collectorMode)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return ForegroundSample.Inactive(observedAtUtc, collectorMode);
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return ForegroundSample.Inactive(observedAtUtc, collectorMode);
        }

        return ForegroundSample.Active(pid, observedAtUtc, collectorMode);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _callback = null;
        _onForegroundChanged = null;
    }

    private void HandleWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != EventSystemForeground || _onForegroundChanged is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (hwnd == IntPtr.Zero)
        {
            _onForegroundChanged(ForegroundSample.Inactive(now, "hook"));
            return;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            _onForegroundChanged(ForegroundSample.Inactive(now, "hook"));
            return;
        }

        _onForegroundChanged(ForegroundSample.Active(pid, now, "hook"));
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
