# RetroBar Developer Notes

## Logging

### Log location

```
%LocalAppData%\RetroBar\Logs\<yyyy-MM-dd_HHmmssfff>.log
```

In powershell,
```
dir $env:LOCALAPPDATA\retrobar
```

For example: `C:\Users\YourName\AppData\Local\RetroBar\Logs\2024-01-15_143022123.log`

One file is written per run. Files older than 7 hours are deleted on startup.

Output also goes to the console (stdout) when RetroBar is launched from a terminal.

### Log severity

By default only `Info` level and above is written. To enable `Debug`-level output, turn on **Debug Logging** in RetroBar's Properties window (or set `"DebugLogging": true` in `settings.json`). This is handled in `ManagedShellLogger.cs` — it sets `ShellLogger.Severity` to `Debug` when the setting is on.

| Severity | Default on? | When to use |
|----------|-------------|-------------|
| Debug    | No          | Verbose tracing — icon add/modify/delete, tray window events, message forwarding |
| Info     | Yes         | Key lifecycle events — system tray icons arriving, startup retry, service transitions |
| Warning  | Yes         | Recoverable unexpected state — Explorer toolbar not found, COM call degraded |
| Error    | Yes         | Failures with caught exceptions |
| Fatal    | Yes         | Unrecoverable errors |

### Relevant log messages for tray icon debugging

When diagnosing missing tray icons at startup, look for these lines (visible at default Info severity):

```
TrayService: Sending TaskbarCreated message
TrayService: Retrying TaskbarCreated to recover any slow-starting tray icons
NotificationArea: System icon added: <Title> GUID: <guid> Hidden: <bool>
NotificationArea: System icon removed: <Title> GUID: <guid>
ExplorerTrayService: Could not find Explorer tray toolbar; existing icons will not be pre-populated
```

If a system icon (battery, network, etc.) never shows an "added" line, the service that owns it never responded to `TaskbarCreated` — either the retry did not help or Windows is managing the icon differently (e.g. UWP-hosted on Win11).

Enable Debug logging to also see every icon add/modify/delete and tray window Z-order events.

### Settings file location

```
%LocalAppData%\RetroBar\settings.json
```
