# Backend API Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the C# agent's `BackendClient`, contracts, and pipeline services with the backend's actual REST API spec so every HTTP call uses the correct method, path, and JSON shape.

**Architecture:** Five independent fix sets, applied bottom-up: (1) enrollment + token auth, (2) signal upload, (3) status poll + decision actions, (4) feature batch upload, (5) liveness probes. Each task compiles and tests independently.

**Tech Stack:** C# 12 / .NET 8, `System.Net.Http.Json`, xunit 2.8, `Microsoft.Extensions.DependencyInjection`, `System.Runtime.InteropServices` (P/Invoke for Windows lock/sleep).

## Global Constraints

- Target framework: `net8.0-windows`  
- No new NuGet packages — use only what is already referenced  
- All `[DllImport]` calls stay in `Shared.Utilities` to avoid duplicating P/Invoke across files  
- Max POST body: 4 MiB — batch sizes must respect this  
- Tests live in `tests/EndpointSignalAgent.Tests/`; run with `dotnet test` from repo root  
- `BackendOptions.UseBackend = false` must still allow simulated/offline execution for every changed path  
- Label strings in `StatusDecision.Label` are not final — treat them as freeform strings, and dispatch known values (`"allow"`, `"deny"`, `"lock"`, `"force_sleep"`, `"unknown"`) case-insensitively  

---

## File Map

**Created:**
- `src/Shared/Utilities/WindowsActions.cs` — static P/Invoke wrapper (lock, sleep); replaces duplicated `NativeMethods` nested classes
- `src/Bootstrap/Identity/DeviceTokenStore.cs` — thread-safe singleton for raw Bearer token
- `src/Bootstrap/Backend/BearerTokenHandler.cs` — `DelegatingHandler` that injects `Authorization: Bearer` header
- `src/SignalCollection/Services/SignalUploadService.cs` — background service reading spool and calling `POST /send`
- `src/Shared/Contracts/FeaturesContracts.cs` — `FeaturesRequest` / `FeaturesResponse` / `FeatureWindowRow`

**Modified:**
- `src/Shared/Contracts/EnrollmentContracts.cs` — updated to match spec
- `src/Shared/Contracts/SignalBatchContracts.cs` — updated to match spec
- `src/Shared/Contracts/StatusContracts.cs` — updated to full decision struct
- `src/Bootstrap/Backend/BackendClient.cs` — all three methods fixed + `PostFeaturesAsync` + `CheckReadyAsync` + `CheckLiveAsync`
- `src/Bootstrap/Configuration/BackendOptions.cs` — remove `FeatureRowCsvPath`, add `HealthzPath`, `ReadyzPath`, `SignalUploadBatchSize`, `SignalUploadIntervalSeconds`
- `src/Bootstrap/Identity/EnrollmentStore.cs` — store token, write to `DeviceTokenStore`
- `src/Shared/Handlers/IDecisionHandler.cs` — updated signature to `StatusDecision`
- `src/Shared/Handlers/DefaultDecisionHandler.cs` — dispatches `lock` / `force_sleep` via `WindowsActions`
- `src/Shared/Services/DecisionProcessorService.cs` — `Channel<StatusDecision>`
- `src/Shared/Services/StatusPollService.cs` — `GET /status?device_id=...` + updated queue type
- `src/Shared/State/AgentState.cs` — adds `CurrentDecision` property
- `src/FeatureExtraction/Services/FeatureCsvStreamService.cs` — migrates from CSV to `POST /features` JSON
- `src/Bootstrap/AgentHostBootstrap.cs` — registers `DeviceTokenStore`, `BearerTokenHandler`, `SignalUploadService`
- `appsettings.json` — removes `FeatureRowCsvPath`, adds `HealthzPath`, `ReadyzPath`, `SignalUploadBatchSize`, `SignalUploadIntervalSeconds`

**Tests created:**
- `tests/EndpointSignalAgent.Tests/DefaultDecisionHandlerTests.cs`
- `tests/EndpointSignalAgent.Tests/DeviceTokenStoreTests.cs`

---

## Task 1: Enrollment Contracts + Token Auth

**Files:**
- Create: `src/Shared/Utilities/WindowsActions.cs`
- Create: `src/Bootstrap/Identity/DeviceTokenStore.cs`
- Create: `src/Bootstrap/Backend/BearerTokenHandler.cs`
- Modify: `src/Shared/Contracts/EnrollmentContracts.cs`
- Modify: `src/Bootstrap/Identity/EnrollmentStore.cs`
- Modify: `src/Bootstrap/Backend/BackendClient.cs` (EnrollAsync only)
- Modify: `src/Bootstrap/AgentHostBootstrap.cs` (register token store + handler)
- Test: `tests/EndpointSignalAgent.Tests/DeviceTokenStoreTests.cs`

**Interfaces:**
- Produces:
  - `DeviceTokenStore.Set(string token)` / `DeviceTokenStore.Get() → string?`
  - `EnrollmentStore` stores token in `enrollment.json`; calls `DeviceTokenStore.Set` after enroll
  - All `BackendClient` HTTP calls gain `Authorization: Bearer <token>` when token is available

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/DeviceTokenStoreTests.cs
using EndpointSignalAgent.Bootstrap.Identity;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class DeviceTokenStoreTests
{
    [Fact]
    public void Get_ReturnsNull_WhenNotSet()
    {
        var store = new DeviceTokenStore();
        Assert.Null(store.Get());
    }

    [Fact]
    public void Get_ReturnsToken_AfterSet()
    {
        var store = new DeviceTokenStore();
        store.Set("tok_abc123");
        Assert.Equal("tok_abc123", store.Get());
    }

    [Fact]
    public void Set_Overwrites_PreviousToken()
    {
        var store = new DeviceTokenStore();
        store.Set("old");
        store.Set("new");
        Assert.Equal("new", store.Get());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ --filter "DeviceTokenStoreTests" -v n`  
Expected: compile error — `DeviceTokenStore` not found

- [ ] **Step 3: Create `WindowsActions.cs`**

```csharp
// src/Shared/Utilities/WindowsActions.cs
using System.Runtime.InteropServices;

namespace EndpointSignalAgent.Shared.Utilities;

public static class WindowsActions
{
    public static void LockWorkstation() => NativeMethods.LockWorkStation();

    public static void ForceSleep() => NativeMethods.SetSuspendState(false, false, false);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("PowrProf.dll")]
        internal static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
```

- [ ] **Step 4: Create `DeviceTokenStore.cs`**

```csharp
// src/Bootstrap/Identity/DeviceTokenStore.cs
namespace EndpointSignalAgent.Bootstrap.Identity;

public sealed class DeviceTokenStore
{
    private volatile string? _token;

    public string? Get() => _token;

    public void Set(string token) => _token = token;
}
```

- [ ] **Step 5: Create `BearerTokenHandler.cs`**

```csharp
// src/Bootstrap/Backend/BearerTokenHandler.cs
using EndpointSignalAgent.Bootstrap.Identity;

namespace EndpointSignalAgent.Bootstrap.Backend;

public sealed class BearerTokenHandler(DeviceTokenStore tokenStore) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = tokenStore.Get();
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
```

- [ ] **Step 6: Update `EnrollmentContracts.cs`**

Replace the entire file:

```csharp
// src/Shared/Contracts/EnrollmentContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record EnrollRequest(
    [property: JsonPropertyName("device_id")]    string? DeviceId,
    [property: JsonPropertyName("hostname")]     string? Hostname,
    [property: JsonPropertyName("os")]           string? Os,
    [property: JsonPropertyName("agent_version")] string? AgentVersion
);

public sealed record EnrollResponse(
    [property: JsonPropertyName("device_id")]      string DeviceId,
    [property: JsonPropertyName("token")]          string Token,
    [property: JsonPropertyName("report_seconds")] int ReportSeconds
);
```

- [ ] **Step 7: Update `BackendClient.EnrollAsync`**

Replace the `EnrollAsync` method body in `src/Bootstrap/Backend/BackendClient.cs`:

```csharp
public async Task<EnrollResponse> EnrollAsync(CancellationToken ct)
{
    if (!_opts.UseBackend)
    {
        var simulatedId = Guid.NewGuid().ToString("D");
        _logger.LogInformation("Enrollment simulated (Backend:UseBackend=false). DeviceId={DeviceId}", simulatedId);
        return new EnrollResponse(DeviceId: simulatedId, Token: "simulated-token", ReportSeconds: 60);
    }

    try
    {
        var req = new EnrollRequest(
            DeviceId: null,
            Hostname: Environment.MachineName,
            Os: Environment.OSVersion.ToString(),
            AgentVersion: typeof(BackendClient).Assembly.GetName().Version?.ToString()
        );
        _logger.LogDebug("Enrolling to {Url}", $"{_opts.BaseUrl}{_opts.EnrollPath}");

        var resp = await _http.PostAsJsonAsync(_opts.EnrollPath, req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Enrollment failed {Status}: {Error}", (int)resp.StatusCode, errorContent);
            throw new HttpRequestException($"Enroll failed status {(int)resp.StatusCode}: {errorContent}");
        }

        var enrollResp = await resp.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken: ct);
        if (enrollResp is null || string.IsNullOrWhiteSpace(enrollResp.DeviceId))
        {
            _logger.LogWarning("Enrollment response missing device ID");
            throw new HttpRequestException("Enroll failed: invalid response");
        }

        _logger.LogInformation("Enrolled device {DeviceId}", enrollResp.DeviceId);
        return enrollResp;
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError(ex, "HTTP error during enrollment to {Url}", $"{_opts.BaseUrl}{_opts.EnrollPath}");
        throw;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unexpected error during enrollment");
        throw;
    }
}
```

- [ ] **Step 8: Update `EnrollmentStore.cs` to save token**

In `EnrollmentStore.cs`, update the `EnrollmentData` record, `Start` method call to `EnrollAsync`, and `SaveEnrollmentAsync`. The store now depends on `DeviceTokenStore`:

Replace the entire file `src/Bootstrap/Identity/EnrollmentStore.cs`:

```csharp
using EndpointSignalAgent.Bootstrap.Backend;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EndpointSignalAgent.Bootstrap.Identity;

public interface IEnrollmentStore
{
    Task<string> GetIdAsync(CancellationToken ct);
}

public sealed class EnrollmentStore : IEnrollmentStore
{
    private readonly BackendClient _backend;
    private readonly DeviceTokenStore _tokenStore;
    private readonly ILogger<EnrollmentStore> _logger;
    private readonly string _enrollmentPath = Path.Combine("spool", "enrollment.json");
    private readonly TaskCompletionSource<string> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _started;

    public EnrollmentStore(
        BackendClient backend,
        DeviceTokenStore tokenStore,
        ILogger<EnrollmentStore> logger)
    {
        _backend = backend;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var existing = await LoadEnrollmentAsync();
                if (existing is not null)
                {
                    _logger.LogInformation("Loaded existing enrollment: {DeviceId}", existing.DeviceId);
                    _tokenStore.Set(existing.Token);
                    _tcs.TrySetResult(existing.DeviceId);
                    return;
                }

                var retryDelay = TimeSpan.FromSeconds(5);
                const double maxRetryDelaySec = 60.0;
                var attempt = 0;

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        attempt++;
                        _logger.LogDebug("Enrollment attempt #{Attempt}", attempt);

                        var resp = await _backend.EnrollAsync(ct);

                        await SaveEnrollmentAsync(resp.DeviceId, resp.Token);
                        _tokenStore.Set(resp.Token);
                        _tcs.TrySetResult(resp.DeviceId);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx)
                    {
                        _logger.LogWarning(
                            "Enrollment attempt #{Attempt} failed (SocketError: {SocketError}). Retrying in {Delay}s",
                            attempt, socketEx.SocketErrorCode, retryDelay.TotalSeconds);
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, maxRetryDelaySec));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Enrollment attempt #{Attempt} failed, retrying in {Delay}s",
                            attempt, retryDelay.TotalSeconds);
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, maxRetryDelaySec));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Enrollment process cancelled");
                _tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in enrollment process");
                _tcs.TrySetException(ex);
            }
        }, ct);
    }

    public Task<string> GetIdAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);

    private async Task<EnrollmentData?> LoadEnrollmentAsync()
    {
        try
        {
            if (!File.Exists(_enrollmentPath)) return null;

            var json = await File.ReadAllTextAsync(_enrollmentPath);
            var data = JsonSerializer.Deserialize<EnrollmentData>(json);

            if (data is null || string.IsNullOrWhiteSpace(data.DeviceId) || string.IsNullOrWhiteSpace(data.Token))
            {
                _logger.LogWarning("Invalid or legacy enrollment file — re-enrolling");
                return null;
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enrollment file");
            return null;
        }
    }

    private async Task SaveEnrollmentAsync(string deviceId, string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_enrollmentPath)!);
            var data = new EnrollmentData(deviceId, token, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_enrollmentPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save enrollment file (continuing anyway)");
        }
    }

    private sealed record EnrollmentData(string DeviceId, string Token, DateTimeOffset EnrolledAt);
}

public sealed class EnrollOnStartupService(
    EnrollmentStore store,
    ILogger<EnrollOnStartupService> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting enrollment service");
        store.Start(stoppingToken);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 9: Register `DeviceTokenStore` and `BearerTokenHandler` in `AgentHostBootstrap.cs`**

In `AgentHostBootstrap.cs`, find `builder.Services.AddSingleton<IAgentIdentity, AgentIdentity>();` and add before it:

```csharp
builder.Services.AddSingleton<DeviceTokenStore>();
builder.Services.AddTransient<BearerTokenHandler>();
```

Then update the `AddHttpClient<BackendClient>` call to attach the handler:

```csharp
builder.Services.AddHttpClient<BackendClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    if (opts.UseBackend)
    {
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    }
})
.AddHttpMessageHandler<BearerTokenHandler>();
```

Also update the named client registration the same way:

```csharp
builder.Services.AddHttpClient("BackendClient", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    if (opts.UseBackend)
    {
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    }
})
.AddHttpMessageHandler<BearerTokenHandler>();
```

- [ ] **Step 10: Verify project builds**

Run: `dotnet build EndpointSignalAgent.csproj`  
Expected: 0 errors

- [ ] **Step 11: Run new tests**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ --filter "DeviceTokenStoreTests" -v n`  
Expected: 3 tests pass

- [ ] **Step 12: Run full test suite**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ -v n`  
Expected: all existing tests pass

- [ ] **Step 13: Commit**

```bash
git add src/Shared/Utilities/WindowsActions.cs \
        src/Bootstrap/Identity/DeviceTokenStore.cs \
        src/Bootstrap/Backend/BearerTokenHandler.cs \
        src/Shared/Contracts/EnrollmentContracts.cs \
        src/Bootstrap/Identity/EnrollmentStore.cs \
        src/Bootstrap/Backend/BackendClient.cs \
        src/Bootstrap/AgentHostBootstrap.cs \
        tests/EndpointSignalAgent.Tests/DeviceTokenStoreTests.cs
git commit -m "feat: fix enrollment contract to match backend spec + Bearer token auth"
```

---

## Task 2: Signal Batch Contracts + Signal Upload Service

**Files:**
- Modify: `src/Shared/Contracts/SignalBatchContracts.cs`
- Modify: `src/Bootstrap/Backend/BackendClient.cs` (SendAsync)
- Modify: `src/Bootstrap/Configuration/BackendOptions.cs` (add upload batch/interval config)
- Create: `src/SignalCollection/Services/SignalUploadService.cs`
- Modify: `src/Bootstrap/AgentHostBootstrap.cs` (register SignalUploadService)
- Modify: `appsettings.json`

**Interfaces:**
- Consumes: `BackendClient` (Task 1), `IEnrollmentStore`, `ISignalProvider`, `IOptions<BackendOptions>`
- Produces: `SignalUploadService` (BackgroundService) — registered in Normal mode

- [ ] **Step 1: Update `SignalBatchContracts.cs`**

Replace the entire file:

```csharp
// src/Shared/Contracts/SignalBatchContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record SignalBatchRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("signals")]   IReadOnlyList<SignalEvent> Signals
);

public sealed record SignalBatchResponse(
    [property: JsonPropertyName("accepted")] int Accepted
);
```

- [ ] **Step 2: Update `BackendClient.SendAsync`**

Replace the `SendAsync` method in `src/Bootstrap/Backend/BackendClient.cs`:

```csharp
public async Task<SignalBatchResponse?> SendAsync(SignalBatchRequest req, CancellationToken ct)
{
    if (!_opts.UseBackend)
    {
        _logger.LogDebug("Signal batch for device {DeviceId} (simulated, count={Count})",
            req.DeviceId, req.Signals.Count);
        return new SignalBatchResponse(Accepted: req.Signals.Count);
    }

    try
    {
        _logger.LogDebug("Sending {Count} signals for device {DeviceId}", req.Signals.Count, req.DeviceId);
        var resp = await _http.PostAsJsonAsync(_opts.SendPath, req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Signal send failed {Status}: {Error}", (int)resp.StatusCode, err);
            throw new HttpRequestException($"Send failed status {(int)resp.StatusCode}: {err}");
        }

        return await resp.Content.ReadFromJsonAsync<SignalBatchResponse>(cancellationToken: ct);
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError(ex, "HTTP error sending signals to {Url}", $"{_opts.BaseUrl}{_opts.SendPath}");
        throw;
    }
}
```

- [ ] **Step 3: Add batch config to `BackendOptions.cs`**

Add two properties to `BackendOptions`:

```csharp
public int SignalUploadBatchSize { get; set; } = 500;
public int SignalUploadIntervalSeconds { get; set; } = 30;
```

- [ ] **Step 4: Update `appsettings.json`**

Add to the `"Backend"` section:

```json
"SignalUploadBatchSize": 500,
"SignalUploadIntervalSeconds": 30
```

- [ ] **Step 5: Create `SignalUploadService.cs`**

```csharp
// src/SignalCollection/Services/SignalUploadService.cs
using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.SignalCollection.Services;

public sealed class SignalUploadService(
    ILogger<SignalUploadService> logger,
    IEnrollmentStore enrollment,
    ISignalProvider signalProvider,
    BackendClient backend,
    IOptions<BackendOptions> backendOptions)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = backendOptions.Value;
        if (!opts.UseBackend)
        {
            logger.LogInformation("SignalUploadService disabled (Backend:UseBackend=false)");
            return;
        }

        var deviceId = await enrollment.GetIdAsync(stoppingToken);
        logger.LogInformation("SignalUploadService started for device {DeviceId}", deviceId);

        var interval = TimeSpan.FromSeconds(opts.SignalUploadIntervalSeconds);
        var backoff = TimeSpan.FromSeconds(5);
        const double backoffMaxSec = 120.0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken);

                try
                {
                    var signals = await signalProvider.CollectAsync(stoppingToken);
                    if (signals.Count == 0) continue;

                    var batched = signals.Count <= opts.SignalUploadBatchSize
                        ? (IReadOnlyList<SignalEvent>)signals
                        : signals.Take(opts.SignalUploadBatchSize).ToList();

                    var req = new SignalBatchRequest(DeviceId: deviceId, Signals: batched);
                    var resp = await backend.SendAsync(req, stoppingToken);

                    logger.LogDebug("Uploaded {Count} signals, accepted={Accepted}",
                        batched.Count, resp?.Accepted ?? 0);

                    backoff = TimeSpan.FromSeconds(5);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Signal upload failed; retrying in {Backoff}s", backoff.TotalSeconds);
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMaxSec));
                }
            }
        }
        catch (OperationCanceledException) { }

        logger.LogInformation("SignalUploadService stopped");
    }
}
```

- [ ] **Step 6: Register `SignalUploadService` in Normal mode**

In `AgentHostBootstrap.cs`, inside the `if (!isDatasetMode)` block, add after `builder.Services.AddHostedService<DecisionProcessorService>()`:

```csharp
builder.Services.AddHostedService<SignalUploadService>();
```

- [ ] **Step 7: Verify build**

Run: `dotnet build EndpointSignalAgent.csproj`  
Expected: 0 errors

- [ ] **Step 8: Run full test suite**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ -v n`  
Expected: all tests pass

- [ ] **Step 9: Commit**

```bash
git add src/Shared/Contracts/SignalBatchContracts.cs \
        src/Bootstrap/Backend/BackendClient.cs \
        src/Bootstrap/Configuration/BackendOptions.cs \
        src/SignalCollection/Services/SignalUploadService.cs \
        src/Bootstrap/AgentHostBootstrap.cs \
        appsettings.json
git commit -m "feat: fix signal batch contracts to match spec + add SignalUploadService"
```

---

## Task 3: Status GET + Full Decision Struct + Label Actions

**Files:**
- Modify: `src/Shared/Contracts/StatusContracts.cs`
- Modify: `src/Bootstrap/Backend/BackendClient.cs` (PollStatusAsync)
- Modify: `src/Shared/Handlers/IDecisionHandler.cs`
- Modify: `src/Shared/Handlers/DefaultDecisionHandler.cs`
- Modify: `src/Shared/Services/DecisionProcessorService.cs`
- Modify: `src/Shared/Services/StatusPollService.cs`
- Modify: `src/Shared/State/AgentState.cs`
- Modify: `src/Bootstrap/AgentHostBootstrap.cs` (channel type)
- Test: `tests/EndpointSignalAgent.Tests/DefaultDecisionHandlerTests.cs`

**Interfaces:**
- Consumes: `WindowsActions` (Task 1), `IAgentState`
- Produces:
  - `StatusDecision` record — full backend response shape
  - `IDecisionHandler.Handle(StatusDecision)` — dispatches `lock` / `force_sleep` actions
  - `IAgentState.CurrentDecision` property

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/DefaultDecisionHandlerTests.cs
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Handlers;
using EndpointSignalAgent.Shared.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class DefaultDecisionHandlerTests
{
    private static StatusDecision MakeDecision(string label) => new(
        DeviceId: "test-device",
        Label: label,
        Score: 0.9,
        Reason: "test",
        ModelVersion: "v1",
        WindowStartTs: 1718668800,
        DecidedAtMs: 1718668830000,
        TtlSeconds: 60);

    [Fact]
    public void Handle_Allow_UpdatesState_DoesNotCallActions()
    {
        var state = new AgentState();
        var lockCalled = false;
        var sleepCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "allow",
            state: state,
            decision: MakeDecision("allow"),
            lockWorkstation: () => lockCalled = true,
            forceSleep: () => sleepCalled = true);

        Assert.Equal("allow", state.CurrentDecision?.Label);
        Assert.False(lockCalled);
        Assert.False(sleepCalled);
    }

    [Fact]
    public void Handle_Lock_CallsLockAndUpdatesState()
    {
        var state = new AgentState();
        var lockCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "lock",
            state: state,
            decision: MakeDecision("lock"),
            lockWorkstation: () => lockCalled = true,
            forceSleep: () => { });

        Assert.True(lockCalled);
        Assert.Equal("lock", state.CurrentDecision?.Label);
    }

    [Fact]
    public void Handle_ForceSleep_CallsSleepAndUpdatesState()
    {
        var state = new AgentState();
        var sleepCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "force_sleep",
            state: state,
            decision: MakeDecision("force_sleep"),
            lockWorkstation: () => { },
            forceSleep: () => sleepCalled = true);

        Assert.True(sleepCalled);
        Assert.Equal("force_sleep", state.CurrentDecision?.Label);
    }

    [Fact]
    public void Handle_Unknown_UpdatesState_DoesNotCallActions()
    {
        var state = new AgentState();
        var lockCalled = false;
        var sleepCalled = false;

        DefaultDecisionHandler.DispatchLabel(
            label: "unknown",
            state: state,
            decision: MakeDecision("unknown"),
            lockWorkstation: () => lockCalled = true,
            forceSleep: () => sleepCalled = true);

        Assert.Equal("unknown", state.CurrentDecision?.Label);
        Assert.False(lockCalled);
        Assert.False(sleepCalled);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ --filter "DefaultDecisionHandlerTests" -v n`  
Expected: compile error — `StatusDecision`, `DispatchLabel`, `CurrentDecision` not found

- [ ] **Step 3: Update `StatusContracts.cs`**

Replace the entire file:

```csharp
// src/Shared/Contracts/StatusContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record StatusDecision(
    [property: JsonPropertyName("device_id")]       string DeviceId,
    [property: JsonPropertyName("label")]           string Label,
    [property: JsonPropertyName("score")]           double Score,
    [property: JsonPropertyName("reason")]          string Reason,
    [property: JsonPropertyName("model_version")]   string ModelVersion,
    [property: JsonPropertyName("window_start_ts")] long WindowStartTs,
    [property: JsonPropertyName("decided_at_ms")]   long DecidedAtMs,
    [property: JsonPropertyName("ttl_seconds")]     int TtlSeconds
);

public static class KnownLabels
{
    public const string Allow      = "allow";
    public const string Deny       = "deny";
    public const string Lock       = "lock";
    public const string ForceSleep = "force_sleep";
    public const string Unknown    = "unknown";
}
```

- [ ] **Step 4: Update `AgentState.cs`**

Replace the entire file:

```csharp
// src/Shared/State/AgentState.cs
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.Shared.State;

public interface IAgentState
{
    int GetReportSecondsOrDefault(int fallbackSeconds);
    void TrySetReportSeconds(int seconds);
    StatusDecision? CurrentDecision { get; }
    void SetDecision(StatusDecision decision);
}

public sealed class AgentState : IAgentState
{
    private int _reportSeconds;
    private volatile StatusDecision? _currentDecision;

    public StatusDecision? CurrentDecision => _currentDecision;

    public int GetReportSecondsOrDefault(int fallbackSeconds)
    {
        var v = Volatile.Read(ref _reportSeconds);
        return v > 0 ? v : fallbackSeconds;
    }

    public void TrySetReportSeconds(int seconds)
    {
        if (seconds is >= 1 and <= 3600)
            Interlocked.Exchange(ref _reportSeconds, seconds);
    }

    public void SetDecision(StatusDecision decision) => _currentDecision = decision;
}
```

- [ ] **Step 5: Update `IDecisionHandler.cs`**

Replace the entire file:

```csharp
// src/Shared/Handlers/IDecisionHandler.cs
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.Shared.Handlers;

public interface IDecisionHandler
{
    void Handle(StatusDecision decision);
}
```

- [ ] **Step 6: Update `DefaultDecisionHandler.cs`**

Replace the entire file:

```csharp
// src/Shared/Handlers/DefaultDecisionHandler.cs
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.State;
using EndpointSignalAgent.Shared.Utilities;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Shared.Handlers;

public sealed class DefaultDecisionHandler(
    ILogger<DefaultDecisionHandler> logger,
    IAgentState agentState) : IDecisionHandler
{
    public void Handle(StatusDecision decision)
    {
        logger.LogInformation(
            "Decision received: label={Label} score={Score} reason={Reason}",
            decision.Label, decision.Score, decision.Reason);

        DispatchLabel(
            label: decision.Label,
            state: agentState,
            decision: decision,
            lockWorkstation: WindowsActions.LockWorkstation,
            forceSleep: WindowsActions.ForceSleep);
    }

    internal static void DispatchLabel(
        string label,
        IAgentState state,
        StatusDecision decision,
        Action lockWorkstation,
        Action forceSleep)
    {
        state.SetDecision(decision);

        switch (label.ToLowerInvariant())
        {
            case KnownLabels.Lock:
                lockWorkstation();
                break;
            case KnownLabels.ForceSleep:
                forceSleep();
                break;
            case KnownLabels.Allow:
            case KnownLabels.Deny:
            case KnownLabels.Unknown:
            default:
                break;
        }
    }
}
```

- [ ] **Step 7: Update `DecisionProcessorService.cs`**

Replace `Channel<StatusResponse>` with `Channel<StatusDecision>` throughout:

```csharp
// src/Shared/Services/DecisionProcessorService.cs
using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Shared.Services;

public sealed class DecisionProcessorService(
    ILogger<DecisionProcessorService> logger,
    Channel<StatusDecision> decisionQueue,
    IDecisionHandler handler)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Decision processor started");

        try
        {
            await foreach (var decision in decisionQueue.Reader.ReadAllAsync(stoppingToken))
            {
                handler.Handle(decision);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Decision processor crashed");
        }
        finally
        {
            logger.LogInformation("Decision processor stopped");
        }
    }
}
```

- [ ] **Step 8: Update `BackendClient.PollStatusAsync`**

Change from `POST /status` with body to `GET /status?device_id=...`:

Replace the `PollStatusAsync` method in `src/Bootstrap/Backend/BackendClient.cs`:

```csharp
public async Task<StatusDecision?> PollStatusAsync(string deviceId, CancellationToken ct)
{
    if (!_opts.UseBackend)
    {
        await Task.Delay(50, ct);
        var simulated = new StatusDecision(
            DeviceId: deviceId,
            Label: KnownLabels.Allow,
            Score: 1.0,
            Reason: "simulated",
            ModelVersion: "sim",
            WindowStartTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DecidedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TtlSeconds: 60);
        _logger.LogDebug("Status poll (simulated): label={Label}", simulated.Label);
        return simulated;
    }

    try
    {
        var url = $"{_opts.StatusPath}?device_id={Uri.EscapeDataString(deviceId)}";
        _logger.LogDebug("Polling status: GET {Url}", url);

        var resp = await _http.GetAsync(url, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Status poll failed {Status}: {Error}", (int)resp.StatusCode, err);
            throw new HttpRequestException($"Status failed status {(int)resp.StatusCode}: {err}");
        }

        var decision = await resp.Content.ReadFromJsonAsync<StatusDecision>(cancellationToken: ct);
        _logger.LogDebug("Status poll: label={Label}", decision?.Label ?? "null");
        return decision;
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError(ex, "HTTP error polling status from {Url}", $"{_opts.BaseUrl}{_opts.StatusPath}");
        throw;
    }
}
```

Also remove the `StatusRequest` parameter from the old `PollStatusAsync` — it no longer takes a request object.

- [ ] **Step 9: Update `StatusPollService.cs`**

Replace `StatusResponse` with `StatusDecision` and fix call to `PollStatusAsync`:

```csharp
// src/Shared/Services/StatusPollService.cs
using System.Threading.Channels;
using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Shared.Services;

public sealed class StatusPollService(
    ILogger<StatusPollService> logger,
    Channel<StatusDecision> decisionQueue,
    IOptions<AgentOptions> agentOptions,
    IEnrollmentStore enrollment,
    BackendClient backend)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deviceId = await enrollment.GetIdAsync(stoppingToken);
        logger.LogInformation("Status poller started for device {DeviceId}", deviceId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var decision = await backend.PollStatusAsync(deviceId, stoppingToken);
                    if (decision is not null)
                        await decisionQueue.Writer.WriteAsync(decision, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Status poll failed");
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(agentOptions.Value.StatusPollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            decisionQueue.Writer.TryComplete();
            logger.LogInformation("Status poller stopped");
        }
    }
}
```

- [ ] **Step 10: Update `AgentHostBootstrap.cs` channel registration**

Find the `Channel.CreateBounded<StatusResponse>` singleton registration and change it to `StatusDecision`:

```csharp
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return Channel.CreateBounded<StatusDecision>(new BoundedChannelOptions(opts.DecisionQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = true
    });
});
```

Also update the `DefaultDecisionHandler` registration to include `IAgentState` dependency (it's already resolved via DI since it's constructor injected; just confirm `IAgentState` is registered before `IDecisionHandler`).

- [ ] **Step 11: Verify build**

Run: `dotnet build EndpointSignalAgent.csproj`  
Expected: 0 errors

- [ ] **Step 12: Run new tests**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ --filter "DefaultDecisionHandlerTests" -v n`  
Expected: 4 tests pass

- [ ] **Step 13: Run full test suite**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ -v n`  
Expected: all tests pass

- [ ] **Step 14: Commit**

```bash
git add src/Shared/Contracts/StatusContracts.cs \
        src/Shared/State/AgentState.cs \
        src/Shared/Handlers/IDecisionHandler.cs \
        src/Shared/Handlers/DefaultDecisionHandler.cs \
        src/Shared/Services/DecisionProcessorService.cs \
        src/Shared/Services/StatusPollService.cs \
        src/Bootstrap/Backend/BackendClient.cs \
        src/Bootstrap/AgentHostBootstrap.cs \
        tests/EndpointSignalAgent.Tests/DefaultDecisionHandlerTests.cs
git commit -m "feat: fix GET /status contract + full decision struct + lock/sleep label actions"
```

---

## Task 4: POST /features JSON Batch Endpoint

**Files:**
- Create: `src/Shared/Contracts/FeaturesContracts.cs`
- Modify: `src/Bootstrap/Backend/BackendClient.cs` (add PostFeaturesAsync)
- Modify: `src/FeatureExtraction/Services/FeatureCsvStreamService.cs`
- Modify: `src/Bootstrap/Configuration/BackendOptions.cs` (remove `FeatureRowCsvPath`)
- Modify: `appsettings.json`

**Interfaces:**
- Consumes: `BackendClient` (Task 1), `IFeatureStore`, `IEnrollmentStore`, `IOptions<BackendOptions>`
- Produces: `BackendClient.PostFeaturesAsync(FeaturesRequest, CancellationToken) → FeaturesResponse?`

- [ ] **Step 1: Create `FeaturesContracts.cs`**

```csharp
// src/Shared/Contracts/FeaturesContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record FeaturesRequest(
    [property: JsonPropertyName("device_id")]       string DeviceId,
    [property: JsonPropertyName("feature_version")] string FeatureVersion,
    [property: JsonPropertyName("window_sec")]      int WindowSec,
    [property: JsonPropertyName("rows")]            IReadOnlyList<Dictionary<string, object>> Rows
);

public sealed record FeaturesResponse(
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("rejected")] IReadOnlyList<string> Rejected
);
```

- [ ] **Step 2: Add `PostFeaturesAsync` to `BackendClient.cs`**

Add the following method to `BackendClient`:

```csharp
public async Task<FeaturesResponse?> PostFeaturesAsync(FeaturesRequest req, CancellationToken ct)
{
    if (!_opts.UseBackend)
    {
        _logger.LogDebug("Features batch (simulated, count={Count})", req.Rows.Count);
        return new FeaturesResponse(Accepted: req.Rows.Count, Rejected: []);
    }

    try
    {
        _logger.LogDebug("Posting {Count} feature rows for device {DeviceId}", req.Rows.Count, req.DeviceId);
        var resp = await _http.PostAsJsonAsync(_opts.FeaturesPath, req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Features POST failed {Status}: {Error}", (int)resp.StatusCode, err);
            throw new HttpRequestException($"Features failed status {(int)resp.StatusCode}: {err}");
        }

        return await resp.Content.ReadFromJsonAsync<FeaturesResponse>(cancellationToken: ct);
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError(ex, "HTTP error posting features to {Url}", $"{_opts.BaseUrl}{_opts.FeaturesPath}");
        throw;
    }
}
```

- [ ] **Step 3: Remove `FeatureRowCsvPath` from `BackendOptions.cs`**

Delete the line:

```csharp
public string FeatureRowCsvPath { get; set; } = "/features/row";
```

- [ ] **Step 4: Migrate `FeatureCsvStreamService.cs` to JSON batch**

Replace the entire file:

```csharp
// src/FeatureExtraction/Services/FeatureCsvStreamService.cs
using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Storage;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Polls the local FeatureStore for unsent rows and POSTs them in JSON batches
/// to POST /features. SQLite acts as the write-ahead buffer so no data is lost
/// during disconnects.
/// </summary>
public sealed class FeatureCsvStreamService : BackgroundService
{
    private readonly ILogger<FeatureCsvStreamService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _extractorOptions;
    private readonly IOptions<BackendOptions> _backendOptions;
    private readonly BackendClient _backend;

    public FeatureCsvStreamService(
        ILogger<FeatureCsvStreamService> logger,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> extractorOptions,
        IOptions<BackendOptions> backendOptions,
        BackendClient backend)
    {
        _logger = logger;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _extractorOptions = extractorOptions;
        _backendOptions = backendOptions;
        _backend = backend;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_extractorOptions.Value.Enabled)
        {
            _logger.LogInformation("FeatureCsvStreamService disabled (FeatureExtractor.Enabled=false)");
            return;
        }

        var deviceId = await _enrollment.GetIdAsync(stoppingToken);
        _logger.LogInformation("FeatureCsvStreamService started for device {DeviceId}", deviceId);

        var pollInterval = TimeSpan.FromSeconds(30);
        var backoff = TimeSpan.FromSeconds(5);
        const double backoffMaxSec = 120.0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(pollInterval, stoppingToken);

                try
                {
                    var unsent = await _featureStore.GetUnsentAsync(limit: 100, stoppingToken);
                    if (unsent.Count == 0)
                    {
                        backoff = TimeSpan.FromSeconds(5);
                        continue;
                    }

                    if (!_backendOptions.Value.UseBackend)
                    {
                        var ids = unsent.Select(r => r.Id).ToList();
                        await _featureStore.MarkAsSentAsync(ids, stoppingToken);
                        continue;
                    }

                    // Group by (feature_version, window_sec) — backend requires a single value per request
                    var groups = unsent
                        .GroupBy(r => (r.FeatureVersion, r.WindowSec))
                        .ToList();

                    var sentIds = new List<long>();
                    var anyFailed = false;

                    foreach (var group in groups)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var rows = group
                            .Select(r =>
                            {
                                var row = new Dictionary<string, object>
                                {
                                    ["window_start_ts"] = r.WindowStartTs.ToUnixTimeSeconds()
                                };
                                foreach (var (k, v) in r.Features)
                                    row[k] = v;
                                return row;
                            })
                            .ToList();

                        var req = new FeaturesRequest(
                            DeviceId: deviceId,
                            FeatureVersion: group.Key.FeatureVersion,
                            WindowSec: group.Key.WindowSec,
                            Rows: rows);

                        try
                        {
                            var resp = await _backend.PostFeaturesAsync(req, stoppingToken);
                            _logger.LogDebug("Features batch accepted={Accepted}", resp?.Accepted ?? 0);
                            sentIds.AddRange(group.Select(r => r.Id));
                        }
                        catch (HttpRequestException ex)
                        {
                            _logger.LogWarning(ex, "Features batch failed for version={Version}", group.Key.FeatureVersion);
                            anyFailed = true;
                            break;
                        }
                    }

                    if (sentIds.Count > 0)
                        await _featureStore.MarkAsSentAsync(sentIds, stoppingToken);

                    backoff = anyFailed
                        ? TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMaxSec))
                        : TimeSpan.FromSeconds(5);

                    if (anyFailed)
                    {
                        _logger.LogWarning("Features upload partial failure; retrying in {Backoff}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FeatureCsvStreamService cycle error");
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMaxSec));
                }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("FeatureCsvStreamService stopped");
    }
}
```

- [ ] **Step 5: Update `AgentHostBootstrap.cs` — remove `IHttpClientFactory` injection for FeatureCsvStreamService**

In `AgentHostBootstrap.cs`, check whether `IHttpClientFactory` is still needed after removing the named client use in `FeatureCsvStreamService`. The named client `"BackendClient"` registration can stay if other code uses it; remove if nothing else does.

Search: `grep -rn '"BackendClient"' src/ --include="*.cs"`

If nothing else uses the named client, remove the duplicate `AddHttpClient("BackendClient", ...)` block. Otherwise keep it.

- [ ] **Step 6: Remove `FeatureRowCsvPath` from `appsettings.json`**

In `appsettings.json`, remove the line:
```json
"FeatureRowCsvPath": "/features/row",
```

- [ ] **Step 7: Verify build**

Run: `dotnet build EndpointSignalAgent.csproj`  
Expected: 0 errors

- [ ] **Step 8: Run full test suite**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ -v n`  
Expected: all tests pass

- [ ] **Step 9: Commit**

```bash
git add src/Shared/Contracts/FeaturesContracts.cs \
        src/Bootstrap/Backend/BackendClient.cs \
        src/Bootstrap/Configuration/BackendOptions.cs \
        src/FeatureExtraction/Services/FeatureCsvStreamService.cs \
        src/Bootstrap/AgentHostBootstrap.cs \
        appsettings.json
git commit -m "feat: add POST /features JSON batch endpoint + migrate FeatureCsvStreamService from CSV"
```

---

## Task 5: GET /healthz + GET /readyz Probes

**Files:**
- Modify: `src/Bootstrap/Configuration/BackendOptions.cs` (add `HealthzPath`, `ReadyzPath`)
- Modify: `src/Bootstrap/Backend/BackendClient.cs` (add `CheckLiveAsync`, `CheckReadyAsync`)
- Modify: `src/Bootstrap/Identity/EnrollmentStore.cs` (await readiness before first enroll)
- Modify: `appsettings.json`

**Interfaces:**
- Produces: `BackendClient.CheckLiveAsync(CancellationToken) → bool`, `BackendClient.CheckReadyAsync(CancellationToken) → bool`

- [ ] **Step 1: Add probe paths to `BackendOptions.cs`**

Add two properties:

```csharp
public string HealthzPath { get; set; } = "/healthz";
public string ReadyzPath { get; set; } = "/readyz";
```

- [ ] **Step 2: Update `appsettings.json`**

Add to the `"Backend"` section:

```json
"HealthzPath": "/healthz",
"ReadyzPath": "/readyz"
```

- [ ] **Step 3: Add probe methods to `BackendClient.cs`**

Add both methods:

```csharp
public async Task<bool> CheckLiveAsync(CancellationToken ct)
{
    if (!_opts.UseBackend) return true;
    try
    {
        var resp = await _http.GetAsync(_opts.HealthzPath, ct);
        return resp.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "Liveness probe failed");
        return false;
    }
}

public async Task<bool> CheckReadyAsync(CancellationToken ct)
{
    if (!_opts.UseBackend) return true;
    try
    {
        var resp = await _http.GetAsync(_opts.ReadyzPath, ct);
        return resp.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "Readiness probe failed");
        return false;
    }
}
```

- [ ] **Step 4: Use `CheckReadyAsync` in `EnrollmentStore.Start` before first enroll attempt**

In `EnrollmentStore.Start`, inside the `while (!ct.IsCancellationRequested)` loop, add a readiness wait before the first enroll:

```csharp
// Wait for backend to be ready before first enroll attempt
if (attempt == 1)
{
    while (!ct.IsCancellationRequested && !await _backend.CheckReadyAsync(ct))
    {
        _logger.LogDebug("Backend not ready yet, waiting 5s before enroll...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
}
```

Add this block after `attempt++;` and before the `await _backend.EnrollAsync(ct);` call.

- [ ] **Step 5: Verify build**

Run: `dotnet build EndpointSignalAgent.csproj`  
Expected: 0 errors

- [ ] **Step 6: Run full test suite**

Run: `dotnet test tests/EndpointSignalAgent.Tests/ -v n`  
Expected: all tests pass

- [ ] **Step 7: Commit**

```bash
git add src/Bootstrap/Configuration/BackendOptions.cs \
        src/Bootstrap/Backend/BackendClient.cs \
        src/Bootstrap/Identity/EnrollmentStore.cs \
        appsettings.json
git commit -m "feat: add GET /healthz + GET /readyz probes; await readiness before enroll"
```

---

## Self-Review Checklist

- [x] `POST /enroll` — new contract shape + token stored + Bearer auth on all subsequent requests
- [x] `POST /send` — new contract shape + `SignalUploadService` created to actually call it
- [x] `GET /status` — method changed POST→GET, full `StatusDecision` struct, `lock`/`force_sleep` dispatch wired to P/Invoke
- [x] `POST /features` — `PostFeaturesAsync` added, `FeatureCsvStreamService` migrated from CSV to JSON batch grouped by `(feature_version, window_sec)`
- [x] `GET /healthz` + `GET /readyz` — probe methods added + used in enrollment gate
- [x] `StatusResponse` (old name) removed everywhere — renamed to `StatusDecision`
- [x] `SignalBatchResponse.Success` (old field) replaced with `Accepted: int`
- [x] No placeholder steps — every code block is complete
- [x] `UseBackend = false` simulated path exists for every changed method
- [x] `DeviceGuardService` still compiles — it has its own private `NativeMethods`; `WindowsActions` is additive, no conflict
