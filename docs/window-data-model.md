Here's how RetroBar tracks windows across monitors, virtual desktops, and groups:
1. Per-Monitor Tracking
Each monitor gets its own Taskbar window. WindowManager (RetroBar/Utilities/WindowManager.cs) enumerates screens via AppBarScreen.FromAllScreens() and creates one Taskbar per screen (or just one for primary if ShowMultiMon is off). Each Taskbar stores its AppBarScreen which carries the HMonitor handle.
Every ApplicationWindow tracks which monitor it's on via HMonitor (ManagedShell/src/ManagedShell.WindowsTasks/ApplicationWindow.cs:298-310). This is set by calling MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST) at ApplicationWindow.cs:579-581. On Windows 8+, it's refreshed on every access (bypassing cache) since windows can move freely.
The TasksList filter (RetroBar/Controls/TaskList.xaml.cs:171-208) decides which windows appear on which taskbar:
- AllTaskbars mode: all windows shown everywhere
- SameAsWindowAndPrimary: secondary monitors show only their own windows; primary shows everything
- It compares window.HMonitor against Host.Screen.HMonitor
Monitor changes are detected via HSHELL.MONITORCHANGED shell hook messages (TasksService.cs:479-491) and on pre-Win8 via EVENT_OBJECT_LOCATIONCHANGE hooks (TasksService.cs:704-713), both calling win.SetMonitor().
2. Virtual Desktop Tracking
RetroBar has two separate virtual desktop systems:
RetroBar's own WorkspaceManager (RetroBar/Utilities/WorkspaceManager.cs) — a manual workspace system with 9 workspaces:
- Maintains _windowWorkspaces (Dictionary<IntPtr, int>) mapping window handles to workspace numbers
- New windows are assigned to the current workspace by default
- Switching workspaces hides/shows windows via ShowWindow(hwnd, Hide/Show), tracked in _hiddenByUs
- The TasksList filter calls WorkspaceManager.Instance.IsHiddenByUs(window.Handle) at TaskList.xaml.cs:180 to exclude hidden windows
- Workspace switch triggers taskbarItems.Refresh() at TaskList.xaml.cs:110
Windows native virtual desktops (RetroBar/Utilities/VirtualDesktopHelper.cs):
- Uses undocumented COM interfaces (IVirtualDesktopManagerInternal, IVirtualDesktopManager) to switch/move windows on Win11
- TasksService handles the actual detection: Win10/11 virtual desktop switches generate per-window EVENT_OBJECT_CLOAKED/EVENT_OBJECT_UNCLOAKED events (TasksService.cs:716-747). Cloaked windows get ShowInTaskbar = false; uncloaked ones (either newly visible or on current desktop) get added back.
- EVENT_SYSTEM_DESKTOPSWITCH (TasksService.cs:789-803) handles non-virtual-desktop switches (UAC, lock screen) by refreshing all windows.
- A deferred ScanForNewWindows (TasksService.cs:767-787) catches Win32 apps that don't fire reliable uncloak events.
3. Window Groups (User-Created)
Groups are a RetroBar-specific UI feature in TaskList (RetroBar/Controls/TaskList.xaml.cs:431+), not from Windows:
- TaskGroup (RetroBar/Utilities/TaskGroup.cs) holds a List<ApplicationWindow> and a GroupColor
- Groups are stored in _taskGroups (List<TaskGroup>) — per-taskbar, in-memory only
- Created via drag-and-drop: drag a button onto another, hold 300ms (GroupHoverMs), a provisional group forms with a merged color. On mouse release, CommitGroupFromProvisional() merges/prettifies the groups
- Group members travel together during drag reorder via _dragGroupMemberIndices
- Context menu actions: UngroupWindow, ChangeGroupColor, RemoveGroup, CollapseGroup
- Visual stripe colors applied via btn.SetGroupColor(group.GroupColor)
- Groups are not persisted — they're lost on restart
The "classic" grouping by application is separate — it's done via PropertyGroupDescription("Category") in Tasks.cs:34, which groups taskbar buttons by their app category (set by TaskCategoryProvider).