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

## Enable start at user logon (Task Scheduler)

Use the provided helper script (current user, no admin required in normal cases):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\register-logon-startup.ps1 -ExecutablePath "C:\path\to\EndpointSignalAgent.exe"
```

Optional removal:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\unregister-logon-startup.ps1
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
