# EndpointSignalAgent

## Running as a Windows tray app

1. Build and run:
   ```powershell
   dotnet run --project .\EndpointSignalAgent.csproj
   ```
2. The app starts in the background and shows a tray icon.
3. Right-click the tray icon for:
   - **Status**
   - **Export all features to CSV** (same behavior as Ctrl+O)
   - **Open spool folder**
   - **Pause collection / Resume collection**
   - **Exit**
4. Use **Exit** for graceful shutdown of all hosted services.

## Smooth setup for "start at user logon"

### Option A (recommended for distribution/export): one-click scripts beside the app

When you export/publish the app, include these files next to `EndpointSignalAgent.exe`:
- `install-logon-startup.cmd`
- `remove-logon-startup.cmd`
- (and their `.ps1` counterparts)

Then users can simply double-click:
- `install-logon-startup.cmd` to register startup
- `remove-logon-startup.cmd` to unregister startup

No manual README steps are required for the end user in this mode.

### Option B (from repo): direct scripts

Register startup task:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\register-logon-startup.ps1 -ExecutablePath "C:\path\to\EndpointSignalAgent.exe"
```

Unregister startup task:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\unregister-logon-startup.ps1
```

## Export/publish with startup scripts included

Use this helper to publish and automatically copy setup/remove scripts into the export folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\export-app-with-startup-tools.ps1
```

Optional example with self-contained output:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\export-app-with-startup-tools.ps1 -Runtime win-x64 -SelfContained
```

## Verify tray startup and pipeline behavior

- Sign out/in (or run task manually via Task Scheduler).
- Confirm tray icon appears.
- Open **Status** and verify host is running.
- Verify spool artifacts are updating:
  - `spool\signals.jsonl`
  - `spool\signals.offset`
  - `spool\features.db`
- Use tray **Exit** and verify process ends cleanly.
