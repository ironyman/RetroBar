using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    public class WorkspaceManager
    {
        public static readonly WorkspaceManager Instance = new WorkspaceManager();

        private int _currentWorkspace = 1;
        private readonly Dictionary<IntPtr, int> _windowWorkspaces = new();
        private readonly HashSet<IntPtr> _hiddenByUs = new();
        private readonly HashSet<IntPtr> _pinnedWindows = new();
        private readonly Dictionary<IntPtr, bool> _elevationCache = new();
        private ObservableCollection<ApplicationWindow> _windows;

        // Remembers each workspace's button order (by handle) so that when a workspace's windows
        // are hidden and later re-shown, they can be reinserted in their original relative order
        // instead of whatever order the OS happens to deliver the async show notifications in -
        // which otherwise varies run to run (e.g. with which window ends up foreground) and made
        // the taskbar reorder itself on every switch.
        private readonly Dictionary<int, List<IntPtr>> _workspaceOrder = new();

        public const int WorkspaceCount = 9;
        public int CurrentWorkspace => _currentWorkspace;

        public event EventHandler WorkspaceSwitched;

        private WorkspaceManager() { }

        public void Initialize(ObservableCollection<ApplicationWindow> windows)
        {
            _windows = windows;

            var initialOrder = new List<IntPtr>();
            foreach (ApplicationWindow w in _windows)
            {
                if (!_windowWorkspaces.ContainsKey(w.Handle))
                    _windowWorkspaces[w.Handle] = _currentWorkspace;
                initialOrder.Add(w.Handle);
            }
            _workspaceOrder[_currentWorkspace] = initialOrder;

            _windows.CollectionChanged += Windows_CollectionChanged;
        }

        private void Windows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (ApplicationWindow w in e.NewItems)
                {
                    if (!_windowWorkspaces.ContainsKey(w.Handle))
                        _windowWorkspaces[w.Handle] = _currentWorkspace;

                    // A window reappearing after being hidden for a workspace switch already has
                    // a remembered slot in this workspace's order - only genuinely new windows
                    // need to be appended.
                    var order = GetOrCreateWorkspaceOrder(_currentWorkspace);
                    if (!order.Contains(w.Handle))
                        order.Add(w.Handle);
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (ApplicationWindow w in e.OldItems)
                {
                    // Hiding a window makes the OS fire HSHELL_WINDOWDESTROYED, so the shell
                    // removes it from the collection exactly as if it had closed. If we're the
                    // ones who hid it, the window still exists — keep its workspace assignment
                    // (and its remembered order slot) so we can restore it when switching back.
                    // Only forget genuine closes.
                    if (_hiddenByUs.Contains(w.Handle))
                        continue;

                    int workspace = GetWindowWorkspace(w.Handle);
                    _windowWorkspaces.Remove(w.Handle);
                    _pinnedWindows.Remove(w.Handle);
                    _elevationCache.Remove(w.Handle);

                    if (_workspaceOrder.TryGetValue(workspace, out var order))
                        order.Remove(w.Handle);
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Move)
            {
                // A manual drag-reorder within the current workspace - remember the new order so
                // it's restored the same way after switching away and back.
                _workspaceOrder[_currentWorkspace] = _windows.Select(w => w.Handle).ToList();
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // TasksService.Dispose() (called by ExplorerMonitor on every TaskbarCreated - explorer
                // restart, monitor/DPI changes, etc.) does Windows.Clear(), which raises Reset with no
                // OldItems. None of the per-item cleanup above runs, so anything we were tracking is
                // orphaned unless we sweep it here.
                PruneOrphanedWindows();
            }
        }

        // Drops tracking for any hwnd that isn't currently in the live collection and isn't
        // deliberately hidden by us for another workspace - i.e. it has no legitimate reason to
        // still be here. Left unpruned, these inflate GetWorkspaceWindowCount forever.
        private void PruneOrphanedWindows()
        {
            var live = new HashSet<IntPtr>(_windows.Select(w => w.Handle));

            foreach (var hwnd in _windowWorkspaces.Keys.ToList())
            {
                if (live.Contains(hwnd) || _hiddenByUs.Contains(hwnd))
                    continue;

                _windowWorkspaces.Remove(hwnd);
                _pinnedWindows.Remove(hwnd);
                _elevationCache.Remove(hwnd);
            }

            foreach (var order in _workspaceOrder.Values)
                order.RemoveAll(h => !live.Contains(h) && !_hiddenByUs.Contains(h));
        }

        private List<IntPtr> GetOrCreateWorkspaceOrder(int workspace)
        {
            if (!_workspaceOrder.TryGetValue(workspace, out var order))
            {
                order = new List<IntPtr>();
                _workspaceOrder[workspace] = order;
            }
            return order;
        }

        // Used as a WindowInsertionIndexProvider so that a window being re-shown after a
        // workspace switch lands back in the position it previously held, regardless of the
        // (nondeterministic) order in which the OS delivers the show notifications for the
        // batch of windows being restored. Returns -1 (let the caller decide, e.g. append) for
        // windows with no remembered slot on the current workspace, such as genuinely new windows.
        public int GetInsertionIndex(ApplicationWindow win, IList<ApplicationWindow> windows)
        {
            if (!_workspaceOrder.TryGetValue(_currentWorkspace, out var order))
                return -1;

            int pos = order.IndexOf(win.Handle);
            if (pos < 0)
                return -1;

            int idx = 0;
            foreach (var w in windows)
            {
                int wPos = order.IndexOf(w.Handle);
                if (wPos < 0 || wPos < pos)
                    idx++;
            }
            return idx;
        }

        private static void SetWindowVisible(IntPtr hwnd, bool visible)
        {
            ShowWindow(hwnd, visible ? WindowShowStyle.ShowNoActivate : WindowShowStyle.Hide);
        }

        public int GetWindowWorkspace(IntPtr hwnd)
        {
            return _windowWorkspaces.TryGetValue(hwnd, out int ws) ? ws : 1;
        }

        public bool IsHiddenByUs(IntPtr hwnd) => _hiddenByUs.Contains(hwnd);

        // Elevated processes ignore our ShowWindow(SW_HIDE) calls (UIPI blocks the window
        // messages a lower-integrity process needs to send to change their visibility), so they're
        // effectively stuck showing on every workspace regardless of what we do. Rather than fight
        // that, we treat them as pinned - and since the user has no way to un-stick them either,
        // pinning can't be turned off for these windows.
        public bool IsElevatedWindow(IntPtr hwnd)
        {
            if (_elevationCache.TryGetValue(hwnd, out bool cached))
                return cached;

            bool elevated = ElevationHelper.IsWindowElevated(hwnd);
            _elevationCache[hwnd] = elevated;
            return elevated;
        }

        public bool CanTogglePin(IntPtr hwnd) => !IsElevatedWindow(hwnd);

        public bool IsPinned(IntPtr hwnd) => _pinnedWindows.Contains(hwnd) || IsElevatedWindow(hwnd);

        public void SetPinned(IntPtr hwnd, bool pinned)
        {
            if (IsElevatedWindow(hwnd)) return; // always pinned, can't be changed

            if (pinned)
            {
                if (_pinnedWindows.Add(hwnd) && _hiddenByUs.Remove(hwnd))
                    SetWindowVisible(hwnd, true);
            }
            else
            {
                _pinnedWindows.Remove(hwnd);

                if (GetWindowWorkspace(hwnd) != _currentWorkspace && _hiddenByUs.Add(hwnd))
                    SetWindowVisible(hwnd, false);
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public void MoveWindowToWorkspace(IntPtr hwnd, int workspace)
        {
            if (workspace < 1 || workspace > WorkspaceCount) return;

            _windowWorkspaces[hwnd] = workspace;

            if (!IsPinned(hwnd))
            {
                if (workspace != _currentWorkspace)
                {
                    if (_hiddenByUs.Add(hwnd))
                        SetWindowVisible(hwnd, false);
                }
                else
                {
                    if (_hiddenByUs.Remove(hwnd))
                        SetWindowVisible(hwnd, true);
                }
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public void SwitchToWorkspace(int workspace)
        {
            if (workspace < 1 || workspace > WorkspaceCount) return;
            if (workspace == _currentWorkspace || _windows == null) return;

            ShellLogger.Info($"WorkspaceManager: Switching to workspace {workspace}");
            _currentWorkspace = workspace;

            // Hide currently-visible windows that don't belong to the new workspace. Pinned windows
            // (including elevated ones we can't hide anyway) stay visible on every workspace.
            foreach (var window in _windows.ToList())
            {
                if (GetWindowWorkspace(window.Handle) != workspace && !IsPinned(window.Handle))
                {
                    if (_hiddenByUs.Add(window.Handle))
                        SetWindowVisible(window.Handle, false);
                }
            }

            // Show windows that belong to the new workspace. Hidden windows have already
            // left _windows (the shell treats a hidden window as destroyed), so restore
            // them from our own tracking rather than from the collection.
            foreach (var hwnd in _hiddenByUs.ToList())
            {
                if (GetWindowWorkspace(hwnd) != workspace)
                    continue;

                _hiddenByUs.Remove(hwnd);

                if (IsWindow(hwnd))
                    SetWindowVisible(hwnd, true);
                else
                    _windowWorkspaces.Remove(hwnd); // closed while hidden — forget it
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public int GetWorkspaceWindowCount(int workspace)
        {
            // Count from the persistent map, not _windows: windows on other workspaces
            // are hidden and have been dropped from the shell's collection. Pinned windows show on
            // every workspace, so they count toward all of them, not just their assigned home.
            return _windowWorkspaces.Count(kvp => IsWindow(kvp.Key) && (kvp.Value == workspace || IsPinned(kvp.Key)));
        }

        // Dumps every hwnd WorkspaceManager is tracking, so a mismatch between the count shown in
        // the "Send to Workspace" menu and what's actually visible can be diagnosed from the log
        // (build.ps1 -openlog) instead of guessing. Flags entries whose hwnd is no longer a real
        // window and entries whose tracked hwnd doesn't correspond to a window currently in the
        // shell's collection, which is what a leaked/reused handle looks like.
        public void LogWindowTracking()
        {
            var liveHandles = new HashSet<IntPtr>(_windows?.Select(w => w.Handle) ?? Enumerable.Empty<IntPtr>());

            ShellLogger.Info($"WorkspaceManager: tracking {_windowWorkspaces.Count} hwnd(s), current workspace {_currentWorkspace}, {_hiddenByUs.Count} hidden-by-us");

            foreach (var kvp in _windowWorkspaces.OrderBy(kvp => kvp.Value))
            {
                IntPtr hwnd = kvp.Key;
                bool isWindow = IsWindow(hwnd);
                bool inLiveCollection = liveHandles.Contains(hwnd);
                string title = "<dead handle>";
                string className = "";

                if (isWindow)
                {
                    var titleBuilder = new StringBuilder(256);
                    GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                    title = titleBuilder.ToString();

                    var classBuilder = new StringBuilder(256);
                    GetClassName(hwnd, classBuilder, classBuilder.Capacity);
                    className = classBuilder.ToString();
                }

                string flag = !isWindow ? " [STALE: not a window anymore]"
                    : !inLiveCollection && kvp.Value == _currentWorkspace && !_hiddenByUs.Contains(hwnd) ? " [SUSPECT: valid handle but not in taskbar collection]"
                    : "";

                ShellLogger.Info($"  hwnd=0x{hwnd.ToInt64():X} workspace={kvp.Value} isWindow={isWindow} pinned={IsPinned(hwnd)} elevated={IsElevatedWindow(hwnd)} hiddenByUs={_hiddenByUs.Contains(hwnd)} inLiveCollection={inLiveCollection} class=\"{className}\" title=\"{title}\"{flag}");
            }
        }

        // Pulls every window from every other workspace onto the current one, so nothing
        // stays stranded out of sight. Mirrors MoveWindowToWorkspace's show/hide bookkeeping
        // but fires WorkspaceSwitched once for the whole batch instead of once per window.
        public void GatherWindows()
        {
            bool changed = false;

            foreach (var hwnd in _windowWorkspaces.Keys.ToList())
            {
                if (_windowWorkspaces[hwnd] == _currentWorkspace)
                    continue;

                _windowWorkspaces[hwnd] = _currentWorkspace;
                changed = true;

                if (_hiddenByUs.Remove(hwnd))
                {
                    if (IsWindow(hwnd))
                        SetWindowVisible(hwnd, true);
                    else
                        _windowWorkspaces.Remove(hwnd); // closed while hidden — forget it
                }
            }

            if (changed)
                WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public void ShowAllWindows()
        {
            foreach (var hwnd in _hiddenByUs.ToList())
            {
                _hiddenByUs.Remove(hwnd);
                if (IsWindow(hwnd))
                    SetWindowVisible(hwnd, true);
            }
        }
    }
}
