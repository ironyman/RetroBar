# RetroBar Architecture

## Project Structure

```
RetroBar.sln
RetroBar/
├── Controls/          - WPF UserControls (TaskButton, TaskList, Clock, NotifyIcons, StartButton)
├── Converters/        - WPF value converters for data binding
├── Utilities/         - Core business logic & Windows integration
├── Extensions/        - Extension methods for ManagedShell objects
├── Languages/         - Localization XAML dictionaries
├── Themes/            - UI theme XAML dictionaries
├── Resources/         - Icons and assets
├── App.xaml.cs        - Application entry, manager initialization
├── Program.cs         - Single-instance mutex, STAThread entry
├── Taskbar.xaml.cs    - Main taskbar window
└── WindowManager.cs   - Per-monitor taskbar lifecycle
```

**Target frameworks:** `netcoreapp3.1`, `net480`, `net6.0-windows`

---

## Startup Sequence

**`Program.cs`** — Entry point
- Creates a named mutex `"RetroBar"` (10 attempts, 1s apart) for single-instance enforcement
- Creates `App` and calls `Run()`

**`App.xaml.cs`** — Bootstrap
1. Constructs `ShellManager` (ManagedShell NuGet — the heavy-lifting shell lib)
2. Creates peripheral managers: `ExplorerMonitor`, `StartMenuMonitor`, `DictionaryManager`, `Updater`, `HotkeyManager`
3. Sets rendering mode (software vs hardware), loads theme, creates `WindowManager`
4. On shutdown: disposes all managers gracefully

---

## Key Files & Roles

| File | Role |
|------|------|
| `Taskbar.xaml.cs` | Main taskbar window; inherits `AppBarWindow` (ManagedShell) |
| `WindowManager.cs` | Creates/destroys taskbar windows per monitor; handles display changes |
| `Settings.cs` | Singleton settings object with `INotifyPropertyChanged` |
| `SettingsManager.cs` | JSON serialization to `%AppData%\RetroBar\settings.json` |
| `ExplorerMonitor.cs` | Listens for `TaskbarCreated` message (explorer.exe restart) |
| `StartMenuMonitor.cs` | Polls for Modern/Classic/OpenShell start menu visibility |
| `HotkeyManager.cs` | Registers Win+[0-9]; reads/patches Explorer's hotkey table in memory |
| `LowLevelMouseHook.cs` | `WH_MOUSE_LL` hook for unlocked taskbar edge-dragging |
| `DictionaryManager.cs` | Loads/switches XAML theme & language ResourceDictionaries |
| `Controls/TaskList.xaml.cs` | ItemsControl of taskbar buttons; filters by monitor & settings |
| `Controls/TaskButton.xaml.cs` | Individual window button; handles click, middle-click, context menu |
| `Controls/NotifyIconList.xaml.cs` | System tray icons with collapsible overflow |
| `Controls/StartButton.xaml.cs` | Start button; can spawn floating variant for certain themes |

---

## Settings Data Structure

```csharp
Settings (Singleton, INotifyPropertyChanged, JSON-persisted)
├── Appearance: Theme, Language, TaskbarScale, AllowFontSmoothing
├── Position:   Edge (Top/Bottom/Left/Right), ShowMultiMon, RowCount, TaskbarWidth
├── Behavior:   AutoHide, LockTaskbar, ShowClock, ShowInputLanguage, ShowDesktopButton
├── Icons:      InvertIconsMode, CollapseNotifyIcons, NotifyIconBehaviors[]
├── Tasks:      TaskMiddleClickAction, ShowTaskBadges, MultiMonMode, WinNumHotkeysAction
├── Notify:     ClockClickAction, ShowQuickLaunch, QuickLaunchPath
└── Advanced:   UseSoftwareRendering, DebugLogging, CheckForUpdates, AllowBlurBehind
```

**Key enums:**
- `AppBarEdge` — Top, Bottom, Left, Right
- `MultiMonOption` — AllTaskbars, SameAsWindow, SameAsWindowAndPrimary
- `TaskMiddleClickOption` — DoNothing, OpenNewInstance, CloseTask
- `ClockClickOption` — DoNothing, OpenAeroCalendar, OpenModernCalendar, OpenNotificationCenter
- `NotifyIconBehavior` — HideWhenInactive, AlwaysHide, AlwaysShow, Remove

---

## UI Architecture

**`Taskbar.xaml`** root is `<appbar:AppBarWindow>` from ManagedShell. Layout:

```
┌──────────┬──────────────┬──────────────────────┬──────────────┐
│  Start   │ Quick Launch │  Task Buttons (list)  │  Tray/Clock  │
│  Button  │  (Toolbar)   │  (TaskList control)   │    Icons     │
└──────────┴──────────────┴──────────────────────┴──────────────┘
```

- DataContext is `ShellManager`
- Task list binds to `ShellManager.Tasks` (ObservableCollection)
- Settings bind via `Settings.Instance` property changed notifications
- Task buttons created via ItemsControl DataTemplate → `TaskButton` UserControl
- Button width is dynamic: calculated from available space ÷ visible task count, clamped by theme defaults

---

## OS Integration & Win32 APIs

**AppBar registration (via ManagedShell):**
- `SHAppBarMessage()` — registers the taskbar window as an AppBar with the shell so Windows reserves screen real estate

**Shell hooks:**
- `RegisterShellHookWindow()` / `WM_SHELLHOOKMESSAGE` — receives shell events (window create/destroy/activate/flash) for task tracking
- `RegisterWindowMessage("TaskbarCreated")` — detects explorer.exe restart via `ExplorerMonitor`

**Low-level hooks (P/Invoke):**
- `SetWindowsHookEx(WH_MOUSE_LL, ...)` — tracks mouse position for unlocked taskbar edge-dragging
- `RegisterHotKey()` / `UnregisterHotKey()` — Win+[0-9] and Shift+Win+[0-9] hotkeys
- Hotkey override: reads Explorer's hotkey table from `explorer.exe` process memory (memory-mapped file scan), sends `WMTRAY_UNREGISTERHOTKEY` to unregister Explorer's copies

**Window management P/Invoke:**
- `FindWindowEx()`, `IsWindowVisible()` — locating system windows
- `GetWindowRect()`, `SetWindowPos()` — taskbar and Start menu positioning
- `SendMessage()` / `PostMessage()` — inter-process commands
- `QueryFullProcessImageName()` — resolving process paths for task icons

**WndProc interception:**
- `Taskbar.xaml.cs` overrides `WndProc()` (from `AppBarWindow`)
- Handles: `WM_SYSCOLORCHANGE`, `WM_SETTINGCHANGE` (SPI_SETWORKAREA), display change notifications → triggers taskbar reposition

---

## Task/Window Tracking

1. **ManagedShell's `TasksService`** uses `RegisterShellHookWindow` to receive window lifecycle events
2. Each tracked window becomes an `ApplicationWindow` object (handle, title, icon, state, HMonitor)
3. **`TaskList`** filters `ShellManager.Tasks` collection:
   - Checks `ApplicationWindow.HMonitor` matches the taskbar's screen
   - Respects `MultiMonOption` setting
   - Filters by `ShowInTaskbar`
4. **`TaskButton`** receives `ApplicationWindow` as DataContext:
   - Left click → `BringToFront()` or minimize toggle
   - Middle click → per `TaskMiddleClickAction` setting
   - Right click → context menu (Restore/Minimize/Maximize/Move/Size/Close/End Task)
   - Reports button screen rect back to shell via `ApplicationWindow.GetButtonRect` callback (used for minimize animation targets)

---

## Multi-Monitor Support

- `WindowManager` calls `AppBarScreen.FromAllScreens()` when `ShowMultiMon` is enabled
- One `Taskbar` window created per screen
- Each `TaskList` independently filters windows by `ApplicationWindow.HMonitor`
- `MultiMonOption`:
  - `AllTaskbars` — every window button appears on every monitor
  - `SameAsWindow` — button only on the window's monitor
  - `SameAsWindowAndPrimary` — button on window's monitor + primary
- Display change messages trigger `WindowManager` to close all taskbars and reopen (handles monitor add/remove/resolution change)
- Per-monitor DPI via `ApplicationHighDpiMode="PerMonitorV2"`; layout rounding disabled at non-integer DPI

---

## Shell / Start Menu Integration

**`StartMenuMonitor`** polls every 100ms for:
- Modern (UWP/Immersive) launcher — `IImmersiveLauncher_Win10RS1` / `IImmersiveLauncher_Win81` COM interfaces
- Classic Start menu — `DV2ControlHost` window class
- OpenShell — `CMenuContainer` window class; auto-repositions relative to taskbar edge

**COM Interfaces used:**
- `IImmersiveLauncher_Win10RS1` — `ShowStartView()`, `DismissStartView()`, `IsVisible()`
- `IImmersiveMonitorManager` — per-monitor launcher instance lookup
- `IImmersiveMonitor` — monitor-specific launcher

---

## Theming & Localization

- **Themes**: XAML `ResourceDictionary` files in `Themes/` (built-in) or `%AppData%\RetroBar\Themes\` (user)
- **Languages**: XAML dictionaries in `Languages/`; `"System"` setting auto-detects Windows UI language with English fallback
- `DictionaryManager` swaps dictionaries in `Application.Resources.MergedDictionaries` at runtime for live switching

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `ManagedShell` | 0.0.330 | Core: AppBar, shell hooks, task tracking, tray icons |
| `gong-wpf-dragdrop` | 3.1.1 | Drag-and-drop for Quick Launch |
| `System.Net.Http.Json` | 6.0.2 | Update checking via GitHub API |

**ManagedShell** is the heaviest dependency — it encapsulates all `ITaskbarList`, shell hook registration, AppBar `SHAppBarMessage` calls, and `NotificationArea` (system tray) management. RetroBar sits on top of it as a WPF UI layer with settings, theming, and behavioral logic.

---

## How RetroBar Replaces the Windows Taskbar

RetroBar does not install or patch anything permanently. Each time it runs it dynamically hides the Explorer taskbar, claims the screen edge as an AppBar, and restores everything on exit.

### Step 1 — Hide the Explorer taskbar

In `WindowManager.cs` constructor (line 35):

```csharp
_shellManager.ExplorerHelper.HideExplorerTaskbar = true;
```

`ExplorerHelper` (ManagedShell) finds the two shell taskbar windows by class name and calls `ShowWindow(SW_HIDE)` on each:

| Window class | What it is |
|---|---|
| `Shell_TrayWnd` | The primary monitor Explorer taskbar |
| `Shell_SecondaryTrayWnd` | Secondary monitor Explorer taskbars |

Both windows still exist in memory — they are simply hidden. This is reversible: when RetroBar exits, `WindowManager.Dispose()` sets `HideExplorerTaskbar = false`, which calls `ShowWindow(SW_SHOW)` on each, restoring the original taskbar.

### Step 2 — Claim the screen edge as an AppBar

Each `Taskbar` window inherits from ManagedShell's `AppBarWindow`, which on initialization calls `SHAppBarMessage` with the Windows Shell AppBar API:

| Call | Effect |
|---|---|
| `SHAppBarMessage(ABM_NEW, ...)` | Registers RetroBar with the shell as an AppBar |
| `SHAppBarMessage(ABM_SETPOS, ...)` | Requests exclusive screen-edge real estate (e.g. bottom 30px) |
| `SHAppBarMessage(ABM_QUERYPOS, ...)` | Queries the position the shell actually grants |

After `ABM_SETPOS`, the Windows shell adjusts the **work area** of the monitor (the rectangle available to maximized windows) to exclude the taskbar strip. This is the same mechanism the real taskbar uses, so maximized apps tile correctly against RetroBar.

### Step 3 — Receiving shell notifications

Once registered as an AppBar, the shell sends `WM_APP + 0x35` messages to the `Taskbar` window handle for events such as full-screen apps entering/leaving and work-area changes. `AppBarWindow.WndProc` handles these to auto-hide or reposition.

### Step 4 — Surviving explorer.exe restarts

If explorer.exe crashes or is restarted, it re-creates its taskbar and broadcasts `TaskbarCreated` (a registered window message). `ExplorerMonitor` intercepts this in its hidden `NativeWindow`:

```csharp
// ExplorerMonitor.cs — WndProc
if (m.Msg == WM_TASKBARCREATEDMESSAGE)
{
    _windowManagerRef.ReopenTaskbars();          // hides Explorer taskbar again
    _shellManager.Tasks.Dispose();
    _shellManager.Tasks.Initialize(true);         // re-registers shell hook for window tracking
}
```

`ReopenTaskbars()` closes and re-opens all `Taskbar` windows, which re-triggers `HideExplorerTaskbar = true` and re-registers the AppBar, effectively pushing Explorer back into hiding.

### Full lifecycle summary

```
RetroBar starts
  └─ WindowManager()
       ├─ ExplorerHelper.HideExplorerTaskbar = true
       │    └─ FindWindow("Shell_TrayWnd")  → ShowWindow(SW_HIDE)
       │    └─ FindWindow("Shell_SecondaryTrayWnd") → ShowWindow(SW_HIDE)
       └─ openTaskbars()
            └─ new Taskbar(...) → AppBarWindow base
                 ├─ SHAppBarMessage(ABM_NEW)       ← register with shell
                 ├─ SHAppBarMessage(ABM_QUERYPOS)  ← ask for edge position
                 └─ SHAppBarMessage(ABM_SETPOS)    ← claim edge, shrink work area

Explorer restarts (TaskbarCreated broadcast)
  └─ ExplorerMonitor.WndProc
       └─ ReopenTaskbars() → re-hides Explorer, re-registers AppBar

RetroBar exits
  └─ WindowManager.Dispose()
       ├─ ExplorerHelper.HideExplorerTaskbar = false
       │    └─ ShowWindow(SW_SHOW) on Shell_TrayWnd / Shell_SecondaryTrayWnd
       └─ AppBarWindow cleanup
            └─ SHAppBarMessage(ABM_REMOVE)  ← release screen edge, restore work area
```

---

## No Plugin System

There is no formal plugin API. Extensibility is via:
- **Custom themes** — drop XAML files in `%AppData%\RetroBar\Themes\`
- **Custom languages** — drop XAML localization files in `%AppData%\RetroBar\Languages\`
- **Quick Launch folder** — configurable path in settings
