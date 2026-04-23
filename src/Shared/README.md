# Shared

Cross-cutting contracts, Normal-mode backend interaction services, decision handling,
agent state, and utilities used by multiple modules.

## Files

### `Contracts/SignalEvent.cs`

The legacy wire format for `spool/signals.jsonl` and the backend `/send` endpoint:
`{ ts, type, payload }` where `payload` is `Dictionary<string, string>`.

### `Contracts/SignalBatchContracts.cs`

- `SignalBatchRequest` — `(DeviceId, Data)` where `Data` is a JSON string of `SignalEvent[]`.
- `SignalBatchResponse` — backend response DTO.

### `Contracts/StatusContracts.cs`

- `StatusResponse` — backend `/status` response. Carries `ReportSeconds` (reporting interval
  override) and any `Decision` payload.

### `Contracts/EnrollmentContracts.cs`

- `EnrollmentRequest` — sent to backend `/enroll`.
- `EnrollmentResponse` — contains `DeviceId` assigned by backend.

### `Handlers/IDecisionHandler.cs` / `DefaultDecisionHandler.cs`

`IDecisionHandler.HandleAsync(StatusResponse, ct)` — processes decisions from the backend.

`DefaultDecisionHandler` — current no-op implementation. Logs the received decision.
Replace this to act on backend decisions (e.g., quarantine device, adjust collection rate).

### `Services/StatusPollService.cs`

**Normal mode only.** Polls `Backend/StatusPath` at `AgentOptions.StatusPollSeconds` interval.
Writes `StatusResponse` to `Channel<StatusResponse>`.

### `Services/DecisionProcessorService.cs`

**Normal mode only.** Reads `Channel<StatusResponse>`, calls `IDecisionHandler.HandleAsync`.
Also applies `AgentState.TrySetReportSeconds` from the status response to dynamically adjust the
batch producer interval.

### `State/AgentState.cs`

`IAgentState` — thread-safe reporting interval override.
- `TrySetReportSeconds(int)` — stores the backend-requested interval (valid range 1–3600).
- `GetReportSecondsOrDefault(int default)` — returns override if set, otherwise default.

### `Utilities/ApplicationCategorizer.cs`

Maps application executable names to category strings.

Normalization:
1. Extract filename, strip extension.
2. Remove all non-alphanumeric characters.
3. Convert to lowercase.
4. Ordinal string match against known application name → category table.

Categories: `Browser`, `IDE`, `Terminal`, `Comms`, `Office`, `Media`, `Design`,
`Database`, `Gaming`, `RemoteAccess`, `FileManager`, `Email`, `System`, `Other`.

Default: `Other` for unrecognized executables.

## Invariants

- `IDecisionHandler` is only registered in Normal mode.
- `AgentState` is only registered in Normal mode.
- All contracts in this namespace are used across at least two other namespaces —
  do not move them into a specific module without updating all consumers.
