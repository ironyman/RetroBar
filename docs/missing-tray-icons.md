Debug: ExplorerTrayService: Unable to get items using ITrayNotify: Retrieving the COM class factory for component with CLSID {25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD} failed ... Class not registered (0x80040154)

That's ExplorerTrayService.Run() — with EnableAutoTray on (the default), it wraps new TrayNotify() COM object creation, SetAutoTrayEnabled(false), and the actual icon read (GetTrayItems()) in one try/catch. On this machine the TrayNotify CLSID isn't registered, so the COM instantiation throws immediately and the whole block bails — GetTrayItems() (which reads explorer's existing tray toolbar via window messages, no COM needed) never runs at all. Any icon already sitting in explorer's tray before RetroBar started (wifi, plus whatever other apps got there first) never gets imported. Icons that register after RetroBar starts still show up fine via the normal Shell_NotifyIcon/TaskbarCreated path — matches what you're seeing.

Let me fix ExplorerTrayService.cs so a COM failure only skips the auto-tray toggle, not the actual icon read.


The built-in Wi-Fi icon (and the other pre-existing tray icons) never showing up is a startup pre-population bug, not a live-icon problem.

Here's the mechanism: when RetroBar isn't the actual shell (explorer.exe is, which is your current setup — IsAppRunningAsShell=False in the log), it can't just wait for apps to re-announce themselves. Icons that were already sitting in explorer's system tray before RetroBar launched — Wi-Fi being one, since it's usually connected before you ever start RetroBar — only get picked up by a one-time import step in ExplorerTrayService.Run() (ManagedShell/src/ManagedShell.WindowsTray/ExplorerTrayService.cs) that reads explorer's real tray toolbar directly.

That import step is wrapped in a try/catch that also instantiates a COM object (TrayNotify, CLSID 25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD) used to temporarily disable "auto tray" so hidden icons can be read too. Your log shows:

Unable to get items using ITrayNotify: ... CLSID {25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD} failed ... Class not registered (0x80040154)

That CLSID isn't registered on your machine, so the COM call throws — and because the actual tray-read call (GetTrayItems()) was inside the same try block, the exception skipped it entirely. Result: the whole pre-population step silently did nothing, so Wi-Fi and any other icon that was already running when RetroBar started never got imported. Icons from apps that launch after RetroBar still show up fine, since those go through the normal live Shell_NotifyIcon/TaskbarCreated path, unaffected by this bug.

I've fixed it by decoupling the two: the COM auto-tray toggle now fails independently, while GetTrayItems() always runs regardless. I haven't rebuilt/restarted RetroBar yet since it's currently running (PID 23992) — want me to build and restart it now to verify the fix?

The following conditions are required for RetroBar to resolve tray icons from explorer:
1. Explorer must be running and setup. EarlyWinBReservation must restart explorer fully.
2. RetroBar must run in medium integrity level. Do not run elevated.