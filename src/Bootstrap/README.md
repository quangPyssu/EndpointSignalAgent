# Bootstrap

Wires the entire application together. Contains DI registration, options validation,
identity/enrollment, and the HTTP backend client.

## Files

### `AgentHostBootstrap.cs`

Static class. Single entry point: `BuildHost(args)` → `IHost`.

- Reads `Agent:Mode` from config to determine `Normal` vs `DatasetCollection`.
- Registers and validates all options (`BackendOptions`, `AgentOptions`,
  `FeatureExtractorOptions`, `DatasetCollectionOptions`) via `ValidateOnStart`.
- Creates two bounded `Channel<BroadcastSignal>` (capacity 1000, `Wait` on full):
  - **signalWriterChannel** → `SignalWriterService`
  - **featureExtractorChannel** → `FeatureExtractorService`
- Creates `Channel<SignalBatchRequest>` (capacity from `AgentOptions.OutgoingQueueCapacity`, `DropOldest`).
- Creates `Channel<StatusResponse>` (capacity from `AgentOptions.DecisionQueueCapacity`, `DropOldest`).
- Always-on hosted services: `EnrollOnStartupService`, `SignalWriterService`,
  four collectors, `FeatureExtractorService`, `KeyboardCommandService`.
- Normal-mode-only: `BatchProducerService`, `BatchSendService`, `FeatureUploadService`,
  `FeatureCleanupService`, `StatusPollService`, `DecisionProcessorService`.
- DatasetCollection-mode-only: session/annotation/progress/manifest/export services
  plus two hosted services (`DatasetShutdownHooksService`, `DatasetSessionStartupService`).

**Mode override side effects applied at registration time:**
- `DatasetCollection` → forces `BackendOptions.UseBackend = false`
- `DatasetCollection` → forces `FeatureExtractorOptions.EnableLiveExtraction = false`

### `Configuration/AgentOptions.cs`

Options section: `Agent`

| Property | Default | Valid range |
|---|---|---|
| `Mode` | `"DatasetCollection"` | `"Normal"` \| `"DatasetCollection"` |
| `DefaultReportSeconds` | 10 | 1–3600 |
| `StatusPollSeconds` | 5 | 1–3600 |
| `OutgoingQueueCapacity` | 300 | 10–100000 |
| `DecisionQueueCapacity` | 300 | 10–100000 |

`AgentModes` static class provides `IsValid(mode)` and `IsDatasetCollection(mode)` helpers.

### `Configuration/BackendOptions.cs`

Options section: `Backend`

| Property | Description |
|---|---|
| `UseBackend` | Whether to enable backend HTTP calls |
| `BaseUrl` | Required when `UseBackend=true`; must be absolute URL |
| `EnrollPath` | Path for enrollment POST |
| `SendPath` | Path for signal batch POST |
| `StatusPath` | Path for status GET |
| `FeaturesPath` | Path for feature batch POST |
| `TimeoutSeconds` | HTTP client timeout |

Validation rejects a non-empty `BaseUrl` that is not an absolute URI.

### `Configuration/DatasetCollectionOptions.cs`

Options section: `DatasetCollection`

| Property | Default | Purpose |
|---|---|---|
| `ParticipantId` | `"P001"` | Identifies this participant in exported packages |
| `StudyId` | `"thesis-mature-dataset-v1"` | Study identifier written to manifests |
| `ProtocolVersion` | `"1.0"` | Manifest schema version |
| `SessionAutoStart` | `true` | Auto-start session on launch |
| `RequireSessionMetadata` | `true` | Prompt for metadata on session start |
| `EnableAbnormalTagging` | `true` | Enable abnormal segment tagging |
| `EnableProgressTracking` | `true` | Run `ProgressTrackingService` |
| `DailyActiveHourTarget` | 4.0 | Hours/day goal for completion ratio |
| `WeeklyActiveDayTarget` | 5 | Days/week goal |
| `StudyWeekTarget` | 4 | Total study weeks goal |
| `MinSessionMinutes` | 20 | Minimum session length counted as valid |
| `ExpectedSessionCount` | 16 | Expected completed sessions for 100% |
| `ExpectedAbnormalScenarioCount` | 6 | Distinct abnormal scenarios expected |
| `ExpectedAbnormalMinutesMin` | 60 | Total abnormal minutes for 100% |
| `ExportRoot` | `"exports"` | Directory for exported packages |
| `ManifestRoot` | `"spool/manifests"` | Directory where manifests are written |

### `Identity/AgentIdentity.cs`

Provides the `IAgentIdentity` interface (`DeviceId` property).
Device ID is loaded from `EnrollmentStore` on first access and cached.

### `Identity/EnrollmentStore.cs`

Persists `spool/enrollment.json`. Provides `IEnrollmentStore`.

Key method: `GetIdAsync(ct)` — returns the enrolled device ID.
If no enrollment file exists, blocks until `EnrollOnStartupService` creates one.

### `Backend/BackendClient.cs`

Typed HTTP client wrapping `HttpClient`. Makes enrollment, send, status, and features calls.
Configured with base address and timeout from `BackendOptions`.

## Invariants

- Mode is determined once at startup from config and does not change at runtime.
- `DatasetCollection` mode never starts backend-facing services regardless of `BackendOptions.UseBackend`.
- Options validation runs before any service starts (`ValidateOnStart`); startup fails fast on misconfiguration.
