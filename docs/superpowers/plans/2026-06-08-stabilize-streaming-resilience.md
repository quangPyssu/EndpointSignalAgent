# Agent Stabilization: CSV Streaming, Legacy Removal, Resilience, Device Guard

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert feature output to single-row CSV POSTs, remove legacy test scaffolding, cap storage growth to survive disconnects and long sessions, and add idle-triggered device lock/sleep.

**Architecture:** Four independent change sets that share zero new abstractions: (1) `FeatureCsvStreamService` replaces `FeatureUploadService` — each extracted `FeatureRow` is serialized as a `text/csv` single-row POST; (2) dead code (`HeartbeatSignalProvider`, `KeyboardCommandService`, raw-batch pipeline) is deleted from both files and DI; (3) `FeatureStore` gains WAL mode and `PruneUnsentToCapAsync`, `FeatureCleanupService` enforces a hard unsent-row cap, `SignalWriterService` rotates `signals.jsonl` past 50 MB; (4) `DeviceGuardService` polls idle time via P/Invoke and calls `LockWorkStation`/`SetSuspendState` when configured thresholds are crossed.

**Tech Stack:** C# .NET 8 WinForms tray host, xUnit, Microsoft.Data.Sqlite, `user32.dll` P/Invoke (`LockWorkStation`), `PowrProf.dll` P/Invoke (`SetSuspendState`).

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Delete | `src/SignalCollection/Providers/HeartbeatSignalProvider.cs` | Dead code — not registered, emits unused signal |
| Delete | `src/FeatureExtraction/Services/KeyboardCommandService.cs` | Console-only debug tool — tray app has no console |
| Delete | `src/FeatureExtraction/Services/FeatureUploadService.cs` | Replaced by FeatureCsvStreamService |
| Delete | `src/FeatureExtraction/Contracts/FeatureBatchContracts.cs` | `FeatureBatchRequest`/`FeatureRowDto` no longer used |
| Create | `src/FeatureExtraction/Services/FeatureCsvRowSerializer.cs` | Static helper: FeatureRow → CSV header + data line |
| Create | `src/FeatureExtraction/Services/FeatureCsvStreamService.cs` | BackgroundService: poll unsent rows, POST each as text/csv |
| Modify | `src/FeatureExtraction/Storage/FeatureStore.cs` | Add WAL mode pragma; add `CountUnsentAsync`, `PruneUnsentToCapAsync` |
| Modify | `src/FeatureExtraction/Services/FeatureCleanupService.cs` | Enforce hard cap (500 unsent rows) alongside age cleanup |
| Modify | `src/SignalCollection/Services/SignalWriterService.cs` | Rotate `signals.jsonl` to `.bak` when file exceeds 50 MB |
| Create | `src/Shared/Services/DeviceGuardService.cs` | Poll idle via P/Invoke; lock/sleep at configured thresholds |
| Modify | `src/Bootstrap/Configuration/BackendOptions.cs` | Add `FeatureRowCsvPath = "/features/row"` |
| Modify | `src/Bootstrap/Configuration/AgentOptions.cs` | Add `DeviceGuardOptions` nested class |
| Modify | `src/Bootstrap/AgentHostBootstrap.cs` | Wire new services; remove deleted registrations |
| Modify | `appsettings.json` | Add `Backend.FeatureRowCsvPath`, `Agent.DeviceGuard` section |
| Create | `tests/.../FeatureCsvRowSerializerTests.cs` | Column order, header count, value escaping |
| Create | `tests/.../FeatureStoreCapTests.cs` | WAL mode set, prune reduces count correctly |
| Create | `tests/.../DeviceGuardServiceTests.cs` | Lock/sleep called at threshold, not below |
| Create | `tests/.../SpoolRotationTests.cs` | File renamed to .bak when size exceeds limit |

---

## Task 1: Delete legacy files + unwire DI

No tests. Pure deletion and `AgentHostBootstrap` edits.

**Files:**
- Delete: `src/SignalCollection/Providers/HeartbeatSignalProvider.cs`
- Delete: `src/FeatureExtraction/Services/KeyboardCommandService.cs`
- Delete: `src/FeatureExtraction/Services/FeatureUploadService.cs`
- Delete: `src/FeatureExtraction/Contracts/FeatureBatchContracts.cs`
- Modify: `src/Bootstrap/AgentHostBootstrap.cs:124-175` (hosted service registrations)

- [ ] **Step 1: Delete the four legacy files**

```bash
rm src/SignalCollection/Providers/HeartbeatSignalProvider.cs
rm src/FeatureExtraction/Services/KeyboardCommandService.cs
rm src/FeatureExtraction/Services/FeatureUploadService.cs
rm src/FeatureExtraction/Contracts/FeatureBatchContracts.cs
```

- [ ] **Step 2: Remove references in AgentHostBootstrap.cs**

In `AgentHostBootstrap.cs`, make these changes:

Remove the `KeyboardCommandService` singleton + hosted service registration (two lines, around the `KeyboardCommandService` block):
```csharp
// REMOVE these two lines:
builder.Services.AddSingleton<KeyboardCommandService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KeyboardCommandService>());
```

Remove the `using` directives that now have no target (the compiler will flag them — delete any red `using` lines).

In the `if (!isDatasetMode)` block, remove `BatchProducerService`, `BatchSendService`, and the `Channel<SignalBatchRequest>` singleton. Keep `StatusPollService`, `DecisionProcessorService`, `IAgentState`, `IDecisionHandler`. Also remove `FeatureUploadService` and `FeatureCleanupService` — they will be re-added with their replacements in later tasks.

The `if (!isDatasetMode)` block should end up as:
```csharp
if (!isDatasetMode)
{
    builder.Services.AddSingleton<IAgentState, AgentState>();
    builder.Services.AddSingleton<IDecisionHandler, DefaultDecisionHandler>();
    builder.Services.AddHostedService<StatusPollService>();
    builder.Services.AddHostedService<DecisionProcessorService>();
}
```

Also remove the `Channel.CreateBounded<SignalBatchRequest>` singleton registration (the one with `DropOldest` and `OutgoingQueueCapacity`). Keep the `Channel<StatusResponse>` singleton.

- [ ] **Step 3: Remove unused using directives caused by deletions**

Search for and remove:
- `using EndpointSignalAgent.SignalCollection.Services;` lines that imported `BatchProducerService`/`BatchSendService` if they now compile-error
- `using EndpointSignalAgent.Shared.Contracts;` if `SignalBatchRequest` was the only use (check — `StatusResponse` is also in that namespace, so keep it)

- [ ] **Step 4: Build to confirm no compile errors**

```bash
dotnet build EndpointSignalAgent.slnx
```

Expected: Build succeeds (0 errors). Fix any remaining dangling references before continuing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove legacy test scaffolding (HeartbeatProvider, KeyboardCommand, BatchSend, FeatureUpload)"
```

---

## Task 2: `FeatureCsvRowSerializer` — unit-tested CSV helper

**Files:**
- Create: `src/FeatureExtraction/Services/FeatureCsvRowSerializer.cs`
- Create: `tests/EndpointSignalAgent.Tests/FeatureCsvRowSerializerTests.cs`

### CSV column layout (fixed, 104 columns)

Metadata columns (6): `device_id`, `window_start_ts`, `window_sec`, `slide_sec`, `feature_schema_version`, `extraction_run_id`

Feature columns (98): `FeatureSchema.AllColumns` in declaration order (App → Session → Network → Cross → System).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/FeatureCsvRowSerializerTests.cs
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Services;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class FeatureCsvRowSerializerTests
{
    private static FeatureRow MakeRow(Dictionary<string, object>? features = null) =>
        new(
            Id: 42,
            DeviceId: "dev-001",
            WindowSec: 60,
            WindowStartTs: DateTimeOffset.Parse("2026-06-08T10:00:00Z"),
            FeatureVersion: "1.2",
            WindowProfileId: "W60_S30",
            WindowSizeSec: 60,
            SlideSec: 30,
            EventTimeStart: DateTimeOffset.Parse("2026-06-08T10:00:00Z"),
            EventTimeEnd: DateTimeOffset.Parse("2026-06-08T10:01:00Z"),
            ExtractionRunId: "run-abc",
            FeatureSchemaVersion: "1.2",
            CollectorSchemaVersion: null,
            SourceCounts: new Dictionary<string, int>(),
            Features: features ?? new Dictionary<string, object>(),
            SentFlag: false,
            SentAt: null);

    [Fact]
    public void Header_HasExact104Columns()
    {
        var header = FeatureCsvRowSerializer.Header;
        var cols = header.Split(',');
        Assert.Equal(104, cols.Length);
    }

    [Fact]
    public void Header_StartsWithMetadataColumns()
    {
        var cols = FeatureCsvRowSerializer.Header.Split(',');
        Assert.Equal("device_id", cols[0]);
        Assert.Equal("window_start_ts", cols[1]);
        Assert.Equal("window_sec", cols[2]);
        Assert.Equal("slide_sec", cols[3]);
        Assert.Equal("feature_schema_version", cols[4]);
        Assert.Equal("extraction_run_id", cols[5]);
    }

    [Fact]
    public void Header_FeatureColumnsMatchAllColumnsOrder()
    {
        var cols = FeatureCsvRowSerializer.Header.Split(',');
        var featureCols = cols.Skip(6).ToArray();
        Assert.Equal(FeatureSchema.AllColumns, featureCols);
    }

    [Fact]
    public void SerializeRow_MetadataFieldsCorrect()
    {
        var row = MakeRow();
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        var cols = line.Split(',');

        Assert.Equal("dev-001", cols[0]);
        Assert.Equal("2026-06-08T10:00:00.0000000+00:00", cols[1]);
        Assert.Equal("60", cols[2]);
        Assert.Equal("30", cols[3]);
        Assert.Equal("1.2", cols[4]);
        Assert.Equal("run-abc", cols[5]);
    }

    [Fact]
    public void SerializeRow_MissingFeatureBecomesZero()
    {
        var row = MakeRow(features: new Dictionary<string, object>());
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        var cols = line.Split(',');
        // All 98 feature columns should be "0" when not present
        foreach (var col in cols.Skip(6))
            Assert.Equal("0", col);
    }

    [Fact]
    public void SerializeRow_FeatureValueWrittenInCorrectPosition()
    {
        var features = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["app_switch_count"] = 7.0 // first AllColumns entry
        };
        var row = MakeRow(features);
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        var cols = line.Split(',');
        // Column index 6 = first feature column
        Assert.Equal("7", cols[6]);
    }

    [Fact]
    public void SerializeRow_DeviceIdWithCommaIsQuoted()
    {
        var row = MakeRow() with { DeviceId = "dev,001" };
        var line = FeatureCsvRowSerializer.SerializeRow(row);
        Assert.StartsWith("\"dev,001\"", line);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "FeatureCsvRowSerializerTests" -v n
```

Expected: compile error — `FeatureCsvRowSerializer` not found.

- [ ] **Step 3: Implement `FeatureCsvRowSerializer`**

```csharp
// src/FeatureExtraction/Services/FeatureCsvRowSerializer.cs
using System.Globalization;
using System.Text;
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.Services;

internal static class FeatureCsvRowSerializer
{
    private static readonly string[] MetaColumns =
    [
        "device_id", "window_start_ts", "window_sec", "slide_sec",
        "feature_schema_version", "extraction_run_id"
    ];

    public static readonly string Header =
        string.Join(",", MetaColumns.Concat(FeatureSchema.AllColumns));

    public static string SerializeRow(FeatureRow row)
    {
        var sb = new StringBuilder(512);

        AppendField(sb, row.DeviceId, first: true);
        AppendField(sb, row.WindowStartTs.ToString("O", CultureInfo.InvariantCulture));
        AppendField(sb, row.WindowSec.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, row.SlideSec.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, row.FeatureSchemaVersion);
        AppendField(sb, row.ExtractionRunId);

        foreach (var col in FeatureSchema.AllColumns)
        {
            var value = row.Features.TryGetValue(col, out var v)
                ? FormatFeatureValue(v)
                : "0";
            AppendField(sb, value);
        }

        return sb.ToString();
    }

    private static string FormatFeatureValue(object v) => v switch
    {
        double d  => d.ToString("G", CultureInfo.InvariantCulture),
        float  f  => f.ToString("G", CultureInfo.InvariantCulture),
        int    i  => i.ToString(CultureInfo.InvariantCulture),
        long   l  => l.ToString(CultureInfo.InvariantCulture),
        bool   b  => b ? "1" : "0",
        _         => v?.ToString() ?? "0"
    };

    private static void AppendField(StringBuilder sb, string value, bool first = false)
    {
        if (!first) sb.Append(',');

        if (value.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            sb.Append('"');
            sb.Append(value.Replace("\"", "\"\""));
            sb.Append('"');
        }
        else
        {
            sb.Append(value);
        }
    }
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "FeatureCsvRowSerializerTests" -v n
```

Expected: 7 tests pass, 0 failures.

- [ ] **Step 5: Commit**

```bash
git add src/FeatureExtraction/Services/FeatureCsvRowSerializer.cs \
        tests/EndpointSignalAgent.Tests/FeatureCsvRowSerializerTests.cs
git commit -m "feat: add FeatureCsvRowSerializer — fixed-schema 104-column CSV helper"
```

---

## Task 3: `FeatureCsvStreamService` — one-row-per-POST streaming uploader

**Files:**
- Create: `src/FeatureExtraction/Services/FeatureCsvStreamService.cs`
- Modify: `src/Bootstrap/Configuration/BackendOptions.cs`
- Modify: `src/Bootstrap/AgentHostBootstrap.cs`

No unit test for the HTTP call — the service is tested by integration. The serializer is already tested.

- [ ] **Step 1: Add `FeatureRowCsvPath` to `BackendOptions`**

Open `src/Bootstrap/Configuration/BackendOptions.cs`. Add one property after `FeaturesPath`:

```csharp
public string FeatureRowCsvPath { get; set; } = "/features/row";
```

- [ ] **Step 2: Implement `FeatureCsvStreamService`**

```csharp
// src/FeatureExtraction/Services/FeatureCsvStreamService.cs
using System.Net.Http.Headers;
using System.Text;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Polls the local FeatureStore for unsent rows and POSTs each as a single
/// text/csv row to the backend. Rows are sent serially, oldest first.
/// On backend unavailability, backs off and retries — SQLite acts as the
/// write-ahead buffer so no data is lost during disconnects.
/// </summary>
public sealed class FeatureCsvStreamService : BackgroundService
{
    private readonly ILogger<FeatureCsvStreamService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _extractorOptions;
    private readonly IOptions<BackendOptions> _backendOptions;
    private readonly IHttpClientFactory _httpClientFactory;

    public FeatureCsvStreamService(
        ILogger<FeatureCsvStreamService> logger,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> extractorOptions,
        IOptions<BackendOptions> backendOptions,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _extractorOptions = extractorOptions;
        _backendOptions = backendOptions;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_extractorOptions.Value.Enabled)
        {
            _logger.LogInformation("FeatureCsvStreamService disabled (FeatureExtractor.Enabled=false)");
            return;
        }

        await _enrollment.GetIdAsync(stoppingToken); // wait for enrollment

        _logger.LogInformation("FeatureCsvStreamService started");

        var pollInterval = TimeSpan.FromSeconds(30);
        var backoff = TimeSpan.FromSeconds(5);
        const double backoffMax = 120.0;

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

                    var anyFailed = false;
                    foreach (var row in unsent)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var sent = _backendOptions.Value.UseBackend
                            ? await PostRowAsync(row, stoppingToken)
                            : true; // offline mode: mark sent immediately

                        if (sent)
                        {
                            await _featureStore.MarkAsSentAsync([row.Id], stoppingToken);
                        }
                        else
                        {
                            anyFailed = true;
                            break; // stop this batch; retry next poll cycle
                        }
                    }

                    if (!anyFailed)
                        backoff = TimeSpan.FromSeconds(5);
                    else
                    {
                        _logger.LogWarning("Row upload failed; retrying in {Backoff}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FeatureCsvStreamService cycle error");
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax));
                }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("FeatureCsvStreamService stopped");
    }

    private async Task<bool> PostRowAsync(
        EndpointSignalAgent.FeatureExtraction.Contracts.FeatureRow row,
        CancellationToken ct)
    {
        try
        {
            var csvBody = FeatureCsvRowSerializer.Header + "\n" + FeatureCsvRowSerializer.SerializeRow(row);
            var content = new StringContent(csvBody, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

            using var client = _httpClientFactory.CreateClient("BackendClient");
            using var response = await client.PostAsync(
                _backendOptions.Value.FeatureRowCsvPath, content, ct);

            if (response.IsSuccessStatusCode) return true;

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("CSV row POST failed ({Status}): {Body}", response.StatusCode, body);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "CSV row POST — HTTP error");
            return false;
        }
    }
}
```

- [ ] **Step 3: Register `FeatureCsvStreamService` in `AgentHostBootstrap`**

In `AgentHostBootstrap.cs`, add after the `FeatureExtractorService` registration (applies both modes):

```csharp
builder.Services.AddHostedService<FeatureCsvStreamService>();
builder.Services.AddHostedService<FeatureCleanupService>();
```

Add the required `using` directive at the top of `AgentHostBootstrap.cs`:

```csharp
using EndpointSignalAgent.FeatureExtraction.Services;
```

- [ ] **Step 4: Build**

```bash
dotnet build EndpointSignalAgent.slnx
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/FeatureExtraction/Services/FeatureCsvStreamService.cs \
        src/Bootstrap/Configuration/BackendOptions.cs \
        src/Bootstrap/AgentHostBootstrap.cs
git commit -m "feat: add FeatureCsvStreamService — per-row text/csv POST replaces JSON batch upload"
```

---

## Task 4: SQLite WAL mode + hard unsent-row cap

Prevents `features.db` from bloating when the backend is unreachable for extended periods.

**Files:**
- Modify: `src/FeatureExtraction/Storage/FeatureStore.cs`
- Modify: `src/FeatureExtraction/Services/FeatureCleanupService.cs`
- Create: `tests/EndpointSignalAgent.Tests/FeatureStoreCapTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/FeatureStoreCapTests.cs
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class FeatureStoreCapTests : IDisposable
{
    private readonly string _dbPath;
    private readonly FeatureStore _store;

    public FeatureStoreCapTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cap_test_{Guid.NewGuid():N}.db");
        _store = new FeatureStore(NullLogger<FeatureStore>.Instance, _dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task CountUnsentAsync_ReturnsCorrectCount()
    {
        await StoreRows(3);
        Assert.Equal(3, await _store.CountUnsentAsync());
    }

    [Fact]
    public async Task PruneUnsentToCapAsync_DeletesOldestWhenOverCap()
    {
        await StoreRows(10);
        await _store.PruneUnsentToCapAsync(cap: 5);
        Assert.Equal(5, await _store.CountUnsentAsync());
    }

    [Fact]
    public async Task PruneUnsentToCapAsync_DoesNothingWhenUnderCap()
    {
        await StoreRows(3);
        await _store.PruneUnsentToCapAsync(cap: 10);
        Assert.Equal(3, await _store.CountUnsentAsync());
    }

    [Fact]
    public async Task PruneUnsentToCapAsync_KeepsNewestRows()
    {
        // Store 5 rows with ascending window start timestamps
        var base_ = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
            await _store.StoreAsync(MakeRow(base_.AddSeconds(i * 30)));

        // Prune to 2
        await _store.PruneUnsentToCapAsync(cap: 2);

        var remaining = await _store.GetAllAsync(limit: 10);
        Assert.Equal(2, remaining.Count);
        // Newest two rows should remain
        Assert.Equal(base_.AddSeconds(3 * 30).ToString("O"), remaining[0].WindowStartTs.ToString("O"));
        Assert.Equal(base_.AddSeconds(4 * 30).ToString("O"), remaining[1].WindowStartTs.ToString("O"));
    }

    private async Task StoreRows(int count)
    {
        var base_ = DateTimeOffset.UtcNow;
        for (int i = 0; i < count; i++)
            await _store.StoreAsync(MakeRow(base_.AddSeconds(i * 30)));
    }

    private static FeatureRow MakeRow(DateTimeOffset windowStart) =>
        FeatureRow.CreateNew(
            deviceId: "test-device",
            windowSec: 60,
            windowStartTs: windowStart,
            featureVersion: "1.2",
            windowProfileId: "W60_S30",
            windowSizeSec: 60,
            slideSec: 30,
            eventTimeStart: windowStart,
            eventTimeEnd: windowStart.AddSeconds(60),
            extractionRunId: "run-test",
            featureSchemaVersion: "1.2",
            collectorSchemaVersion: null,
            sourceCounts: new Dictionary<string, int>(),
            features: new Dictionary<string, object>());
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "FeatureStoreCapTests" -v n
```

Expected: compile error — `FeatureStore(logger, path)` constructor overload and `CountUnsentAsync`/`PruneUnsentToCapAsync` not found.

- [ ] **Step 3: Add constructor overload + new methods to `FeatureStore`**

Open `src/FeatureExtraction/Storage/FeatureStore.cs`.

**3a. Add a testable constructor that accepts a custom db path:**

Replace the existing single constructor with:

```csharp
public FeatureStore(ILogger<FeatureStore> logger)
    : this(logger, Path.Combine(Directory.GetCurrentDirectory(), "spool", "features.db"))
{
}

public FeatureStore(ILogger<FeatureStore> logger, string dbPath)
{
    _logger = logger;
    _dbPath = dbPath;
    Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    _logger.LogInformation("FeatureStore initialized with database: {DbPath}", _dbPath);
}
```

**3b. Enable WAL mode in `EnsureInitializedAsync`.** After the `connection.OpenAsync(ct)` call and before the `CREATE TABLE` command, add:

```csharp
// WAL mode: safe for power-loss, better concurrent write performance
using var walCmd = connection.CreateCommand();
walCmd.CommandText = "PRAGMA journal_mode=WAL;";
await walCmd.ExecuteNonQueryAsync(ct);
```

**3c. Add `CountUnsentAsync`:**

```csharp
public async Task<int> CountUnsentAsync(CancellationToken ct = default)
{
    await EnsureInitializedAsync(ct);
    using var connection = new SqliteConnection($"Data Source={_dbPath}");
    await connection.OpenAsync(ct);
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM feature_rows WHERE sent_flag = 0";
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
}
```

**3d. Add `PruneUnsentToCapAsync`** (deletes oldest unsent rows, keeping only the newest `cap` unsent rows):

```csharp
/// <summary>
/// Deletes the oldest unsent rows so at most <paramref name="cap"/> unsent rows remain.
/// Sent rows are unaffected.
/// </summary>
public async Task<int> PruneUnsentToCapAsync(int cap, CancellationToken ct = default)
{
    await EnsureInitializedAsync(ct);
    using var connection = new SqliteConnection($"Data Source={_dbPath}");
    await connection.OpenAsync(ct);

    // Delete all unsent rows except the newest `cap` rows (ordered by window_start_ts DESC)
    using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        DELETE FROM feature_rows
        WHERE sent_flag = 0
          AND id NOT IN (
              SELECT id FROM feature_rows
              WHERE sent_flag = 0
              ORDER BY window_start_ts DESC
              LIMIT @cap
          )";
    cmd.Parameters.AddWithValue("@cap", cap);
    var deleted = await cmd.ExecuteNonQueryAsync(ct);
    if (deleted > 0)
        _logger.LogWarning("Pruned {Count} oldest unsent feature rows to enforce cap of {Cap}", deleted, cap);
    return deleted;
}
```

**3e. Add `CountUnsentAsync` and `PruneUnsentToCapAsync` to the `IFeatureStore` interface:**

```csharp
Task<int> CountUnsentAsync(CancellationToken ct = default);
Task<int> PruneUnsentToCapAsync(int cap, CancellationToken ct = default);
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "FeatureStoreCapTests" -v n
```

Expected: 4 tests pass.

- [ ] **Step 5: Update `FeatureCleanupService` to enforce the hard cap**

Open `src/FeatureExtraction/Services/FeatureCleanupService.cs`.

Replace the class body. Change the cleanup interval from 24h to 1h (to catch bloat faster on long sessions) and add the hard-cap check before the age-based cleanup:

```csharp
public sealed class FeatureCleanupService : BackgroundService
{
    private const int UnsentRowHardCap = 500;
    private readonly ILogger<FeatureCleanupService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(7);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public FeatureCleanupService(ILogger<FeatureCleanupService> logger, IFeatureStore featureStore)
    {
        _logger = logger;
        _featureStore = featureStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FeatureCleanupService started (cap={Cap}, retention={Days}d, interval={H}h)",
            UnsentRowHardCap, _retentionPeriod.TotalDays, _cleanupInterval.TotalHours);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
                try
                {
                    // Hard cap: drop oldest unsent rows when backend is unreachable for too long
                    var unsentCount = await _featureStore.CountUnsentAsync(stoppingToken);
                    if (unsentCount > UnsentRowHardCap)
                        await _featureStore.PruneUnsentToCapAsync(UnsentRowHardCap, stoppingToken);

                    // Age-based: delete old sent rows
                    await _featureStore.DeleteOlderThanAsync(DateTimeOffset.UtcNow - _retentionPeriod, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during feature cleanup");
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogInformation("FeatureCleanupService stopped");
    }
}
```

- [ ] **Step 6: Build**

```bash
dotnet build EndpointSignalAgent.slnx
```

Expected: 0 errors.

- [ ] **Step 7: Run all tests**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ -v n
```

Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/FeatureExtraction/Storage/FeatureStore.cs \
        src/FeatureExtraction/Services/FeatureCleanupService.cs \
        tests/EndpointSignalAgent.Tests/FeatureStoreCapTests.cs
git commit -m "feat: FeatureStore WAL mode + hard unsent-row cap (500) in FeatureCleanupService"
```

---

## Task 5: Spool file rotation in `SignalWriterService`

Prevents `spool/signals.jsonl` from growing without bound. When the file exceeds 50 MB before a write, it is renamed to `signals.jsonl.bak` (overwriting any previous backup) and writing continues to a fresh `signals.jsonl`.

**Files:**
- Modify: `src/SignalCollection/Services/SignalWriterService.cs`
- Create: `tests/EndpointSignalAgent.Tests/SpoolRotationTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/SpoolRotationTests.cs
using EndpointSignalAgent.SignalCollection.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class SpoolRotationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _spoolPath;
    private readonly string _bakPath;

    public SpoolRotationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"spool_rot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _spoolPath = Path.Combine(_dir, "signals.jsonl");
        _bakPath = _spoolPath + ".bak";
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RotateIfNeeded_DoesNotRotate_WhenFileBelowThreshold()
    {
        File.WriteAllText(_spoolPath, "small content");
        SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1_000_000);
        Assert.True(File.Exists(_spoolPath));
        Assert.False(File.Exists(_bakPath));
    }

    [Fact]
    public void RotateIfNeeded_RenamesFile_WhenFileExceedsThreshold()
    {
        File.WriteAllText(_spoolPath, "any content");
        SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1); // 1 byte threshold
        Assert.False(File.Exists(_spoolPath)); // original gone
        Assert.True(File.Exists(_bakPath));    // backup exists
    }

    [Fact]
    public void RotateIfNeeded_OverwritesPreviousBackup()
    {
        File.WriteAllText(_bakPath, "old backup");
        File.WriteAllText(_spoolPath, "new content");
        SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1);
        Assert.Equal("new content", File.ReadAllText(_bakPath));
    }

    [Fact]
    public void RotateIfNeeded_DoesNotThrow_WhenFileDoesNotExist()
    {
        var ex = Record.Exception(() =>
            SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1_000_000));
        Assert.Null(ex);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "SpoolRotationTests" -v n
```

Expected: compile error — `SpoolRotation` not found.

- [ ] **Step 3: Create `SpoolRotation` helper and update `SignalWriterService`**

Add a static helper class at the bottom of `SignalWriterService.cs` (or as a separate file in the same namespace — same file is fine since it's small):

```csharp
// Add at the bottom of SignalWriterService.cs, before the closing namespace brace:

internal static class SpoolRotation
{
    internal const long DefaultThresholdBytes = 50 * 1024 * 1024; // 50 MB

    internal static void RotateIfNeeded(string spoolPath, long thresholdBytes = DefaultThresholdBytes)
    {
        if (!File.Exists(spoolPath)) return;
        if (new FileInfo(spoolPath).Length < thresholdBytes) return;

        var bakPath = spoolPath + ".bak";
        File.Move(spoolPath, bakPath, overwrite: true);
    }
}
```

In `SignalWriterService.ExecuteAsync`, add one line before writing each signal — inside the `await foreach` loop, before the `using var writer = new SpoolFileCollector(...)` line:

```csharp
SpoolRotation.RotateIfNeeded(signal.SpoolPath);
```

The relevant section of `ExecuteAsync` becomes:

```csharp
await foreach (var signal in _reader.ReadAllAsync(stoppingToken))
{
    try
    {
        SpoolRotation.RotateIfNeeded(signal.SpoolPath); // rotate before write if > 50 MB
        using var writer = new SpoolFileCollector(signal.SpoolPath);
        await writer.WriteAsync(
            new SignalEvent(signal.TimestampUtc, signal.Type, signal.Payload),
            stoppingToken);

        if (_writeRawSignals)
        {
            var rawPath = Path.Combine(Path.GetDirectoryName(signal.SpoolPath) ?? "spool", "raw_signals.jsonl");
            SpoolRotation.RotateIfNeeded(rawPath);
            using var rawWriter = new RawSignalFileCollector(rawPath);
            await rawWriter.WriteAsync(BuildRawRecord(signal), stoppingToken);
        }
    }
    catch (Exception ex) { ... }
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "SpoolRotationTests" -v n
```

Expected: 4 tests pass.

- [ ] **Step 5: Build**

```bash
dotnet build EndpointSignalAgent.slnx
```

- [ ] **Step 6: Commit**

```bash
git add src/SignalCollection/Services/SignalWriterService.cs \
        tests/EndpointSignalAgent.Tests/SpoolRotationTests.cs
git commit -m "feat: spool file rotation at 50 MB to prevent unbounded signals.jsonl growth"
```

---

## Task 6: `DeviceGuardService` — idle-triggered lock and sleep

Monitors workstation idle time via P/Invoke and enforces lock/sleep thresholds. Disabled by default; opt-in via `appsettings.json`.

**Files:**
- Create: `src/Shared/Services/DeviceGuardService.cs`
- Modify: `src/Bootstrap/Configuration/AgentOptions.cs`
- Modify: `src/Bootstrap/AgentHostBootstrap.cs`
- Create: `tests/EndpointSignalAgent.Tests/DeviceGuardServiceTests.cs`

- [ ] **Step 1: Add DeviceGuard config to `AgentOptions`**

Open `src/Bootstrap/Configuration/AgentOptions.cs`. Add a nested class and a property:

```csharp
public DeviceGuardOptions DeviceGuard { get; set; } = new();

public sealed class DeviceGuardOptions
{
    /// <summary>Enable idle-based lock/sleep enforcement.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Lock workstation after this many idle seconds. 0 = disabled.</summary>
    public int IdleLockThresholdSec { get; set; } = 3600;

    /// <summary>Sleep device after this many idle seconds. 0 = disabled.</summary>
    public int IdleSleepThresholdSec { get; set; } = 0;

    /// <summary>How often to check idle state (seconds).</summary>
    public int PollIntervalSec { get; set; } = 30;
}
```

- [ ] **Step 2: Write failing tests**

```csharp
// tests/EndpointSignalAgent.Tests/DeviceGuardServiceTests.cs
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class DeviceGuardServiceTests
{
    private static IOptions<AgentOptions> Opts(
        bool enabled = true,
        int lockSec = 600,
        int sleepSec = 0) =>
        Options.Create(new AgentOptions
        {
            DeviceGuard = new AgentOptions.DeviceGuardOptions
            {
                Enabled = enabled,
                IdleLockThresholdSec = lockSec,
                IdleSleepThresholdSec = sleepSec,
                PollIntervalSec = 1
            }
        });

    [Fact]
    public void EvaluateIdle_DoesNothing_WhenBelowAllThresholds()
    {
        var lockCalled = false;
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 100,
            opts: Opts(lockSec: 600, sleepSec: 0).Value.DeviceGuard,
            lockWorkstation: () => lockCalled = true,
            sleep: () => sleepCalled = true);

        Assert.False(lockCalled);
        Assert.False(sleepCalled);
    }

    [Fact]
    public void EvaluateIdle_CallsLock_WhenIdleExceedsLockThreshold()
    {
        var lockCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 700,
            opts: Opts(lockSec: 600, sleepSec: 0).Value.DeviceGuard,
            lockWorkstation: () => lockCalled = true,
            sleep: () => { });

        Assert.True(lockCalled);
    }

    [Fact]
    public void EvaluateIdle_CallsSleep_WhenIdleExceedsSleepThreshold()
    {
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 1300,
            opts: Opts(lockSec: 600, sleepSec: 1200).Value.DeviceGuard,
            lockWorkstation: () => { },
            sleep: () => sleepCalled = true);

        Assert.True(sleepCalled);
    }

    [Fact]
    public void EvaluateIdle_DoesNotCallSleep_WhenSleepThresholdIsZero()
    {
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 9999,
            opts: Opts(lockSec: 600, sleepSec: 0).Value.DeviceGuard,
            lockWorkstation: () => { },
            sleep: () => sleepCalled = true);

        Assert.False(sleepCalled);
    }

    [Fact]
    public void EvaluateIdle_PrefersLock_WhenBothThresholdsExceededButSleepHigher()
    {
        // idle=700, lockSec=600, sleepSec=1200 → only lock threshold exceeded
        var lockCalled = false;
        var sleepCalled = false;

        DeviceGuardService.EvaluateIdle(
            idleSec: 700,
            opts: Opts(lockSec: 600, sleepSec: 1200).Value.DeviceGuard,
            lockWorkstation: () => lockCalled = true,
            sleep: () => sleepCalled = true);

        Assert.True(lockCalled);
        Assert.False(sleepCalled);
    }
}
```

- [ ] **Step 3: Run tests to confirm they fail**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "DeviceGuardServiceTests" -v n
```

Expected: compile error — `DeviceGuardService` not found.

- [ ] **Step 4: Implement `DeviceGuardService`**

```csharp
// src/Shared/Services/DeviceGuardService.cs
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
    /// Pure threshold logic — extracted for testability.
    /// Sleep threshold takes priority if both thresholds exceeded.
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
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return false;
        idleSec = (Environment.TickCount - (int)info.dwTime) / 1000;
        return idleSec >= 0;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")] internal static extern bool LockWorkStation();
        [DllImport("PowrProf.dll")] internal static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
        [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
    }
}
```

- [ ] **Step 5: Register `DeviceGuardService` in `AgentHostBootstrap`**

In `AgentHostBootstrap.cs`, after the `FeatureExtractorService` registration (applies to both modes — the service self-disables when `Enabled=false`):

```csharp
builder.Services.AddHostedService<DeviceGuardService>();
```

Add the using directive:
```csharp
using EndpointSignalAgent.Shared.Services;
```

- [ ] **Step 6: Run tests to confirm they pass**

```bash
dotnet test tests/EndpointSignalAgent.Tests/ --filter "DeviceGuardServiceTests" -v n
```

Expected: 5 tests pass.

- [ ] **Step 7: Build**

```bash
dotnet build EndpointSignalAgent.slnx
```

- [ ] **Step 8: Commit**

```bash
git add src/Shared/Services/DeviceGuardService.cs \
        src/Bootstrap/Configuration/AgentOptions.cs \
        src/Bootstrap/AgentHostBootstrap.cs \
        tests/EndpointSignalAgent.Tests/DeviceGuardServiceTests.cs
git commit -m "feat: DeviceGuardService — idle-triggered lock/sleep via P/Invoke, disabled by default"
```

---

## Task 7: Update `appsettings.json`

Wire up the new config keys so defaults are explicit and the DeviceGuard section appears discoverable.

**Files:**
- Modify: `appsettings.json`

- [ ] **Step 1: Add new config keys to `appsettings.json`**

Open `appsettings.json`. Make two additions:

Under `"Backend"`, add:
```json
"FeatureRowCsvPath": "/features/row"
```

Under `"Agent"`, add the `DeviceGuard` section:
```json
"DeviceGuard": {
  "Enabled": false,
  "IdleLockThresholdSec": 3600,
  "IdleSleepThresholdSec": 0,
  "PollIntervalSec": 30
}
```

The full updated `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "Backend": {
    "UseBackend": false,
    "BaseUrl": "",
    "EnrollPath": "/enroll",
    "SendPath": "/send",
    "StatusPath": "/status",
    "FeaturesPath": "/features",
    "FeatureRowCsvPath": "/features/row",
    "TimeoutSeconds": 30
  },
  "Agent": {
    "Mode": "DatasetCollection",
    "OutgoingQueueCapacity": 1000,
    "DecisionQueueCapacity": 100,
    "DefaultReportSeconds": 60,
    "StatusPollSeconds": 30,
    "DeviceGuard": {
      "Enabled": false,
      "IdleLockThresholdSec": 3600,
      "IdleSleepThresholdSec": 0,
      "PollIntervalSec": 30
    }
  },
  "FeatureExtractor": {
    "Enabled": true,
    "EnableLiveExtraction": false,
    "WindowSizeSeconds": 60,
    "WindowSlideSeconds": 30,
    "MaxEventsPerWindow": 1000
  },
  "DatasetCollection": {
    "Enabled": true,
    "ParticipantId": "P001",
    "StudyId": "thesis-mature-dataset-v1",
    "ProtocolVersion": "1.0",
    "SessionAutoStart": false,
    "RequireSessionMetadata": true,
    "EnableAbnormalTagging": true,
    "EnableProgressTracking": true,
    "DailyActiveHourTarget": 4.0,
    "WeeklyActiveDayTarget": 5,
    "StudyWeekTarget": 4,
    "MinSessionMinutes": 20,
    "ExpectedSessionCount": 16,
    "ExpectedAbnormalScenarioCount": 6,
    "ExpectedAbnormalMinutesMin": 60,
    "ExportRoot": "exports",
    "ManifestRoot": "spool/manifests"
  }
}
```

- [ ] **Step 2: Build + full test run**

```bash
dotnet build EndpointSignalAgent.slnx && dotnet test tests/EndpointSignalAgent.Tests/ -v n
```

Expected: 0 build errors, all tests pass.

- [ ] **Step 3: Final commit**

```bash
git add appsettings.json
git commit -m "chore: expose FeatureRowCsvPath and DeviceGuard config in appsettings.json"
```

---

## Self-Review

### Spec coverage check

| Requirement | Task(s) |
|---|---|
| Single CSV row continuously sent to backend | Task 2 (serializer) + Task 3 (stream service) |
| Kill legacy systems (HeartbeatProvider, KeyboardCommand, BatchSend, JSON batch) | Task 1 |
| Survive backend disconnect (store-and-forward, backoff) | Task 3 (SQLite buffer + backoff) |
| Survive power-out (WAL mode, no in-memory-only state) | Task 4 (WAL pragma) |
| Survive long sessions (infinite bloat prevention) | Task 4 (row cap), Task 5 (spool rotation) |
| Step-up auth: lock device on idle | Task 6 (DeviceGuardService, `IdleLockThresholdSec`) |
| Intentional sleep on idle | Task 6 (DeviceGuardService, `IdleSleepThresholdSec`) |
| Lightweight agent | Task 1 (removed 3 background services + console polling thread) |

### Placeholder check

No TBD, no "add error handling", no "similar to task N". All code blocks are complete.

### Type consistency

- `FeatureCsvRowSerializer.Header` and `SerializeRow` both iterate `FeatureSchema.AllColumns` — same reference, consistent.
- `IFeatureStore.CountUnsentAsync` / `PruneUnsentToCapAsync` added to both interface and implementation.
- `DeviceGuardService.EvaluateIdle` is `internal static` — matches test access modifier.
- `DeviceGuardOptions` nested in `AgentOptions` — matches test helper constructor.
- `FeatureStore(ILogger, string)` constructor used in test matches implementation signature.
