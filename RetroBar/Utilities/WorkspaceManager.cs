using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    public class WorkspaceManager
    {
        // Kept local to RetroBar (not ManagedShell.Interop) deliberately. While locked, the system
        // refuses SetForegroundWindow from every process - we take the lock the instant a switch
        // begins (while we still hold the WM_HOTKEY / taskbar-click foreground right) so that apps
        // which self-activate when un-hidden can't grab the foreground vacuum our hide-loop creates.
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockSetForegroundWindow(uint uLockCode);
        private const uint LSFW_LOCK = 1;
        private const uint LSFW_UNLOCK = 2;
        public static readonly WorkspaceManager Instance = new WorkspaceManager();

        private int _currentWorkspace = 1;
        private readonly Dictionary<IntPtr, int> _windowWorkspaces = new();
        private readonly HashSet<IntPtr> _hiddenByUs = new();

        // Windows we've called ShowWindow on but that the shell hasn't re-added to the collection
        // yet. ShowWindow -> HSHELL_WINDOWCREATED -> Windows.Add is asynchronous; during that gap
        // the window is in neither the collection nor (if we cleared it eagerly) _hiddenByUs, so
        // anything classifying windows during the gap (TaskGroupManager.ReconcileWithSource on a
        // view Reset, TaskList's genuinely-gone check) would misread it as closed and e.g. dissolve
        // its task group. So a window stays in _hiddenByUs until its re-add actually lands; this
        // set tracks which ones are in that in-flight state.
        private readonly HashSet<IntPtr> _pendingShow = new();

        // Per-workspace memory of which window was foreground when we last left that workspace, so
        // switching back can restore focus to it instead of letting Windows pick (which lands on
        // whatever ends up topmost after the batch re-show - usually not the window the user left).
        private readonly Dictionary<int, IntPtr> _lastActiveByWorkspace = new();

        // The window to re-activate once the workspace we're switching TO finishes restoring.
        // Activation waits for the window's re-add (it isn't back in the collection synchronously);
        // cleared once activated, or superseded when a newer switch starts.
        private IntPtr _pendingActivate = IntPtr.Zero;

        // Guards against posting more than one deferred focus-restore flush at a time.
        private bool _activateFlushScheduled;

        // Whether we currently hold the system-wide SetForegroundWindow lock (see LockForeground).
        private bool _foregroundLocked;

        // While we're hiding/showing windows for a workspace switch, Windows fires a cascade of
        // incidental WINDOWACTIVATED events (hiding the foreground window activates the next one in
        // Z-order, which we then hide too, and so on). Those aren't the user choosing a window, so
        // they must not overwrite _lastActiveByWorkspace - otherwise a workspace's remembered focus
        // becomes "last window hidden" instead of the window the user actually left active. Each
        // hide/show bumps this deadline; activation tracking is ignored until it passes.
        private const int ActivationSuppressMs = 500;
        private DateTime _suppressActivationTrackingUntil = DateTime.MinValue;
        private bool ActivationTrackingSuppressed => DateTime.UtcNow < _suppressActivationTrackingUntil;
        private void BumpActivationSuppression() => _suppressActivationTrackingUntil = DateTime.UtcNow.AddMilliseconds(ActivationSuppressMs);

        // True from the moment a switch starts hiding/showing windows until ~ActivationSuppressMs
        // after the last one. The taskbar uses this to also suppress button animations for the
        // whole switch, including the collapsed-group reveal that a re-activated member triggers
        // slightly after its window re-adds.
        public bool IsSwitching => ActivationTrackingSuppressed;

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

        public void Initialize(ObservableCollection<ApplicationWindow> windows, TasksService tasksService)
        {
            _windows = windows;

            var initialOrder = new List<IntPtr>();
            foreach (ApplicationWindow w in _windows)
            {
                if (!_windowWorkspaces.ContainsKey(w.Handle))
                    _windowWorkspaces[w.Handle] = _currentWorkspace;
                initialOrder.Add(w.Handle);
                TrackActivation(w);
                if (w.State == ApplicationWindow.WindowState.Active)
                    _lastActiveByWorkspace[_currentWorkspace] = w.Handle;
            }
            _workspaceOrder[_currentWorkspace] = initialOrder;

            _windows.CollectionChanged += Windows_CollectionChanged;
            tasksService.WindowActivated += TasksService_WindowActivated;
        }

        // Fires on every HSHELL_WINDOWACTIVATED/RUDEAPPACTIVATED - i.e. whenever some window becomes
        // the foreground window, for any reason, not just switches we initiated. Normally a window
        // that isn't on the current workspace can't become foreground at all - hiding it (SW_HIDE)
        // takes it out of the running entirely. But some apps bypass that: a single-instance app
        // relaunched from a shortcut finds its own existing (hidden) window and calls
        // ShowWindow(SW_RESTORE)/SetForegroundWindow on it directly, without going through us. That
        // leaves the window genuinely on screen and foreground while WorkspaceManager still has it
        // recorded on its old workspace and marked hidden-by-us - which hides its task button (see
        // TaskList.Tasks_Filter) even though the window itself is visible. Since the window is
        // unmistakably here now, follow it: reassign it to the current workspace instead of leaving
        // its button hidden.
        private void TasksService_WindowActivated(object sender, WindowEventArgs e)
        {
            IntPtr hwnd = e.Window?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero || !_hiddenByUs.Contains(hwnd))
                return;

            int oldWorkspace = GetWindowWorkspace(hwnd);
            if (oldWorkspace == _currentWorkspace)
                return;

            ShellLogger.Debug($"WorkspaceManager: 0x{hwnd.ToInt64():X} \"{e.Window.Title}\" came to foreground outside our control - moving it from workspace {oldWorkspace} to {_currentWorkspace}");

            if (_workspaceOrder.TryGetValue(oldWorkspace, out var oldOrder))
                oldOrder.Remove(hwnd);

            _windowWorkspaces[hwnd] = _currentWorkspace;
            _hiddenByUs.Remove(hwnd);
            _pendingShow.Remove(hwnd);

            var order = GetOrCreateWorkspaceOrder(_currentWorkspace);
            if (!order.Contains(hwnd))
                order.Add(hwnd);

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        // Continuously remember, per workspace, the most recently activated window - so a later
        // switch back to that workspace can restore focus to it. Tracked on activation (not
        // snapshotted at switch time) so it's correct regardless of how the switch is triggered:
        // a global hotkey leaves the app window active, but clicking a taskbar workspace button can
        // move focus to the taskbar first, at which point no app window reads as active anymore.
        private void TrackActivation(ApplicationWindow w)
        {
            w.PropertyChanged -= Window_PropertyChanged;
            w.PropertyChanged += Window_PropertyChanged;
        }

        private void Window_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ApplicationWindow.State) || sender is not ApplicationWindow w)
                return;

            if (w.State != ApplicationWindow.WindowState.Active)
                return;

            // Ignore activations that are just fallout from our own hide/show during a switch.
            if (ActivationTrackingSuppressed)
            {
                ShellLogger.Debug($"WorkspaceManager: ignoring incidental activation of 0x{w.Handle.ToInt64():X} \"{w.Title}\" during switch");
                return;
            }

            int ws = GetWindowWorkspace(w.Handle);
            _lastActiveByWorkspace[ws] = w.Handle;
            ShellLogger.Debug($"WorkspaceManager: SAVED last-active for workspace {ws}: 0x{w.Handle.ToInt64():X} \"{w.Title}\"");
        }

        private void Windows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (ApplicationWindow w in e.NewItems)
                {
                    TrackActivation(w);

                    // A re-show we issued has landed: only now does the window stop counting as
                    // hidden-by-us (see _pendingShow). If the user switched workspaces again while
                    // the show was still in flight, it no longer belongs here - hide it right back.
                    if (_pendingShow.Remove(w.Handle))
                    {
                        if (GetWindowWorkspace(w.Handle) != _currentWorkspace && !IsPinned(w.Handle))
                        {
                            ShellLogger.Debug($"WorkspaceManager: re-show of 0x{w.Handle.ToInt64():X} landed after another switch - hiding it again");
                            SetWindowVisible(w.Handle, false);
                            continue;
                        }
                        _hiddenByUs.Remove(w.Handle);
                    }

                    if (!_windowWorkspaces.ContainsKey(w.Handle))
                        _windowWorkspaces[w.Handle] = _currentWorkspace;

                    // A window reappearing after being hidden for a workspace switch already has
                    // a remembered slot in this workspace's order - only genuinely new windows
                    // need to be appended.
                    var order = GetOrCreateWorkspaceOrder(_currentWorkspace);
                    if (!order.Contains(w.Handle))
                        order.Add(w.Handle);

                }

                // Once the whole re-show batch has landed, restore focus to the target as the very
                // last thing (see FlushPendingActivate). Doing it per-window mid-batch let a later
                // re-show steal the foreground back.
                if (_pendingShow.Count == 0)
                    ScheduleActivateFlush();
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (ApplicationWindow w in e.OldItems)
                {
                    // This ApplicationWindow instance is being discarded (the shell disposes it on
                    // removal, hidden or closed); drop our activation subscription. A window that's
                    // only hidden keeps its recorded _lastActiveByWorkspace entry and gets a fresh
                    // instance (re-tracked) when it returns.
                    w.PropertyChanged -= Window_PropertyChanged;

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
                    ForgetLastActive(w.Handle);

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

            _pendingShow.RemoveWhere(h => !IsWindow(h));
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
            ShellLogger.Debug($"WorkspaceManager: GetInsertionIndex restoring 0x{win.Handle.ToInt64():X} \"{win.Title}\" to slot {idx} (orderPos {pos} of {order.Count})");
            return idx;
        }

        private static void SetWindowVisible(IntPtr hwnd, bool visible)
        {
            ShowWindow(hwnd, visible ? WindowShowStyle.ShowNoActivate : WindowShowStyle.Hide);
        }

        // Re-shows a window we previously hid. The hwnd deliberately stays in _hiddenByUs until
        // the shell actually re-adds it (see _pendingShow); Windows_CollectionChanged completes
        // the handoff. Returns true if a show was issued.
        private bool ShowHiddenWindow(IntPtr hwnd)
        {
            if (!_hiddenByUs.Contains(hwnd)) return false;

            if (!IsWindow(hwnd))
            {
                // closed while hidden — forget it
                _hiddenByUs.Remove(hwnd);
                _pendingShow.Remove(hwnd);
                _windowWorkspaces.Remove(hwnd);
                return false;
            }

            _pendingShow.Add(hwnd);
            BumpActivationSuppression();
            SetWindowVisible(hwnd, true);
            return true;
        }

        // Hides a window, cancelling any in-flight re-show for it (rapid workspace switching can
        // hide a window again before its previous show has landed).
        private void HideWindow(IntPtr hwnd)
        {
            bool wasPending = _pendingShow.Remove(hwnd);
            if (_hiddenByUs.Add(hwnd) || wasPending)
            {
                BumpActivationSuppression();
                SetWindowVisible(hwnd, false);
            }
        }

        // Forgets a window as any workspace's last-active - called when it's genuinely closed so
        // switching back never tries to focus a dead handle.
        private void ForgetLastActive(IntPtr hwnd)
        {
            if (_pendingActivate == hwnd) _pendingActivate = IntPtr.Zero;
            foreach (var ws in _lastActiveByWorkspace.Where(kvp => kvp.Value == hwnd).Select(kvp => kvp.Key).ToList())
                _lastActiveByWorkspace.Remove(ws);
        }

        // Focus restoration after a workspace switch is deferred through here. It's posted at
        // Background priority so it runs after the synchronous re-show batch and after any app that
        // grabs the foreground for itself when it's un-hidden (VS Code and friends). Restoring focus
        // mid-batch, as we used to, let one of those later activations steal the foreground back -
        // and since activation-tracking suppression had lapsed by the time that stray activation
        // landed, the wrong window got recorded as the workspace's last-active, so the next switch
        // back restored the wrong window too. Deferring makes our activation the last word.
        // Takes the system-wide foreground lock. Must be called at the very start of a switch, while
        // RetroBar still holds the right to change the foreground (the WM_HOTKEY token for Win+1-9,
        // or being the foreground window itself for a taskbar-button click) - the lock can only be
        // acquired by a process that could set the foreground right now. While held, no process
        // (including a self-activating VS Code being un-hidden) can steal focus. Windows auto-releases
        // the lock on genuine user input, so a missed unlock self-heals; we still always pair it.
        private void LockForeground()
        {
            if (_foregroundLocked) return;
            if (LockSetForegroundWindow(LSFW_LOCK))
            {
                _foregroundLocked = true;
                ShellLogger.Debug("WorkspaceManager: locked SetForegroundWindow for switch");
            }
            else
            {
                ShellLogger.Debug($"WorkspaceManager: LockSetForegroundWindow(LOCK) failed (err {Marshal.GetLastWin32Error()}) - relying on deferred flush");
            }
        }

        private void UnlockForeground()
        {
            if (!_foregroundLocked) return;
            LockSetForegroundWindow(LSFW_UNLOCK);
            _foregroundLocked = false;
            ShellLogger.Debug("WorkspaceManager: unlocked SetForegroundWindow");
        }

        private void ScheduleActivateFlush()
        {
            if (_activateFlushScheduled || _pendingActivate == IntPtr.Zero)
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _activateFlushScheduled = true;
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(FlushPendingActivate));
        }

        private void FlushPendingActivate()
        {
            _activateFlushScheduled = false;

            IntPtr target = _pendingActivate;
            if (target == IntPtr.Zero)
            {
                // Nothing to restore (target was cleared by a newer switch or a close) - release the
                // lock we took for the switch so the system isn't left frozen.
                UnlockForeground();
                return;
            }

            // More re-shows are still in flight - wait. The Add handler re-schedules us once the
            // batch fully drains, so we always activate strictly last. Keep the lock held meanwhile.
            if (_pendingShow.Count > 0)
                return;

            var live = _windows?.FirstOrDefault(w => w.Handle == target);
            if (live == null || !IsWindow(target))
            {
                ShellLogger.Debug($"WorkspaceManager: pending focus target 0x{target.ToInt64():X} no longer live - skipping restore");
                _pendingActivate = IntPtr.Zero;
                UnlockForeground();
                return;
            }

            _pendingActivate = IntPtr.Zero;

            // Release the lock immediately before we activate: while held it refuses *every*
            // SetForegroundWindow, ours included. The apps that would steal focus already tried and
            // were denied during the locked hide/show, and they don't retry, so unlocking here and
            // activating right away lets our target win uncontested.
            UnlockForeground();
            live.BringToFront();

            // Record the restored window directly as the authoritative outcome, and keep suppression
            // alive so a self-activation a stubborn app fires immediately afterwards is treated as
            // incidental instead of overwriting the memory we just restored. Genuine user clicks are
            // still captured - either after suppression lapses, or by the foreground snapshot taken
            // when this workspace is next left.
            BumpActivationSuppression();
            _lastActiveByWorkspace[_currentWorkspace] = target;
            ShellLogger.Debug($"WorkspaceManager: flushed focus restore to 0x{target.ToInt64():X} \"{live.Title}\"; foreground now 0x{GetForegroundWindow().ToInt64():X}");
        }

        // Safety net for _pendingShow: normally an entry is cleared when the shell re-adds the
        // window (Windows_CollectionChanged). But if a ShowWindow produced no re-add - e.g. the
        // window was already visible, or is no longer taskbar-eligible - the entry would linger,
        // and with it a stale _hiddenByUs entry (inflating GetWorkspaceWindowCount and pinning
        // group membership across resets). Run at each switch so staleness is bounded to one
        // switch: complete the handoff for windows that are actually visible now, and drop dead
        // handles outright. (Note: the window is genuinely on-screen either way - a lingering
        // entry never means a window stuck invisible, only bookkeeping drift.)
        private void DrainStalePendingShows()
        {
            foreach (var hwnd in _pendingShow.ToList())
            {
                if (!IsWindow(hwnd))
                {
                    _pendingShow.Remove(hwnd);
                    _hiddenByUs.Remove(hwnd);
                    _windowWorkspaces.Remove(hwnd);
                    ForgetLastActive(hwnd);
                    ShellLogger.Debug($"WorkspaceManager: DrainStalePendingShows dropped dead in-flight show 0x{hwnd.ToInt64():X}");
                }
                else if (IsWindowVisible(hwnd))
                {
                    _pendingShow.Remove(hwnd);
                    if (GetWindowWorkspace(hwnd) == _currentWorkspace || IsPinned(hwnd))
                        _hiddenByUs.Remove(hwnd);
                    ShellLogger.Debug($"WorkspaceManager: DrainStalePendingShows completed handoff for already-visible 0x{hwnd.ToInt64():X}");
                }
            }
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
                if (_pinnedWindows.Add(hwnd))
                    ShowHiddenWindow(hwnd);
            }
            else
            {
                _pinnedWindows.Remove(hwnd);

                if (GetWindowWorkspace(hwnd) != _currentWorkspace)
                    HideWindow(hwnd);
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
                    HideWindow(hwnd);
                else
                    ShowHiddenWindow(hwnd);
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public void SwitchToWorkspace(int workspace)
        {
            if (workspace < 1 || workspace > WorkspaceCount) return;
            if (workspace == _currentWorkspace || _windows == null) return;

            ShellLogger.Info($"WorkspaceManager: Switching to workspace {workspace}");

            // Take the foreground lock now, while we're still inside the WM_HOTKEY / click handler and
            // therefore still hold the right to change the foreground. From here until the deferred
            // flush activates our target (or the no-target branch below), no other process can grab
            // focus - which is what stops apps that self-activate on un-hide from stealing the vacuum
            // our hide-loop opens. UnlockForeground is idempotent, so clear any lock a prior switch
            // left behind (e.g. if its flush never ran) before taking a fresh one.
            UnlockForeground();
            LockForeground();

            int leaving = _currentWorkspace;

            // Snapshot the departing workspace's live taskbar order straight from the on-screen
            // collection. The incremental order tracking (Add/Move) drifts from the true display
            // order: GroupAfterParent (ParentWindowHelper.FindInsertionIndex) inserts a newly opened
            // window right after the active window - i.e. mid-list - while our Add handler only ever
            // appends it to _workspaceOrder. That divergence is what made a window (e.g. a second
            // VS Code window grouped next to its sibling) jump to the end after a round-trip. Re-reading
            // the real order at leave time is authoritative, so switching back restores exactly what
            // was on screen regardless of the async order the OS re-adds windows in.
            _workspaceOrder[leaving] = _windows.Select(w => w.Handle).ToList();

            // Pin down the window to restore focus to, from the current foreground. Continuous
            // activation tracking (Window_PropertyChanged) can miss the user's last click when it
            // lands inside the post-switch suppression window, leaving this workspace's last-active
            // stale or unset. The live foreground window is ground truth - but only trust it when it's
            // one of our app windows actually on the workspace we're leaving. If focus is on the
            // taskbar itself (e.g. the switch came from clicking a taskbar workspace button) it won't
            // be in _windows, so we fall back to the continuously-tracked value.
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && _windows.Any(w => w.Handle == fg) && GetWindowWorkspace(fg) == leaving)
            {
                _lastActiveByWorkspace[leaving] = fg;
                ShellLogger.Debug($"WorkspaceManager: snapshot last-active for workspace {leaving} at leave: 0x{fg.ToInt64():X}");
            }

            // The workspace we're leaving keeps its last-active window in _lastActiveByWorkspace,
            // maintained continuously by Window_PropertyChanged (see TrackActivation) and pinned down
            // by the foreground snapshot above.

            // Any activation queued by a previous switch is stale now. Force-complete any re-shows
            // still marked in-flight from an earlier switch (bounds _pendingShow - see below).
            _pendingActivate = IntPtr.Zero;
            DrainStalePendingShows();

            _currentWorkspace = workspace;

            // Hide currently-visible windows that don't belong to the new workspace. Pinned windows
            // (including elevated ones we can't hide anyway) stay visible on every workspace.
            foreach (var window in _windows.ToList())
            {
                if (GetWindowWorkspace(window.Handle) != workspace && !IsPinned(window.Handle))
                    HideWindow(window.Handle);
            }

            // Show windows that belong to the new workspace. Hidden windows have already
            // left _windows (the shell treats a hidden window as destroyed), so restore
            // them from our own tracking rather than from the collection. Each stays in
            // _hiddenByUs until its re-add actually lands (see _pendingShow).
            foreach (var hwnd in _hiddenByUs.ToList())
            {
                if (GetWindowWorkspace(hwnd) != workspace)
                    continue;

                ShowHiddenWindow(hwnd);
            }

            // Queue restoring focus to whichever window was last active here. The actual activation
            // is always deferred (see FlushPendingActivate) so it runs after the whole re-show batch
            // settles - restoring mid-batch let a later re-show, or an app that self-activates when
            // un-hidden, steal the foreground back. If nothing is being re-shown (target already
            // live, e.g. pinned), the flush scheduled below fires right away.
            if (_lastActiveByWorkspace.TryGetValue(workspace, out IntPtr lastActive) && IsWindow(lastActive))
            {
                _pendingActivate = lastActive;
                ShellLogger.Debug($"WorkspaceManager: queued focus restore for 0x{lastActive.ToInt64():X} (pendingShow={_pendingShow.Contains(lastActive)}, inFlight={_pendingShow.Count})");
                if (_pendingShow.Count == 0)
                    ScheduleActivateFlush();
            }
            else
            {
                ShellLogger.Debug($"WorkspaceManager: no last-active window recorded for workspace {workspace} (or it's gone) - not restoring focus");
                // Nothing will be activated, so no deferred flush runs to release the lock - drop it now.
                UnlockForeground();
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        // Manual escape hatch mirroring TasksService.SweepDeadWindows. A hwnd we've marked
        // _hiddenByUs is deliberately skipped by Windows_CollectionChanged's remove handler (kept
        // alive on the assumption it's only hidden for another workspace, not closed) - so if the
        // shell's WINDOWDESTROYED notification is ever missed for a hidden window, it never gets a
        // chance to reach that check at all, and outlives every other cleanup path (PruneOrphanedWindows
        // only fires on a collection Reset, DrainStalePendingShows only during a switch). This forces
        // the issue by checking IsWindow() directly. Returns the number removed.
        public int SweepDeadWindows()
        {
            var deadHandles = _windowWorkspaces.Keys
                .Concat(_hiddenByUs)
                .Concat(_pendingShow)
                .Distinct()
                .Where(h => !IsWindow(h))
                .ToList();

            foreach (var hwnd in deadHandles)
            {
                _windowWorkspaces.Remove(hwnd);
                _pinnedWindows.Remove(hwnd);
                _elevationCache.Remove(hwnd);
                _hiddenByUs.Remove(hwnd);
                _pendingShow.Remove(hwnd);
                ForgetLastActive(hwnd);
            }

            foreach (var order in _workspaceOrder.Values)
                order.RemoveAll(h => !IsWindow(h));

            if (deadHandles.Count > 0)
                WorkspaceSwitched?.Invoke(this, EventArgs.Empty);

            ShellLogger.Info($"WorkspaceManager: SweepDeadWindows removed {deadHandles.Count} dead hwnd(s)");
            return deadHandles.Count;
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

                ShellLogger.Info($"  hwnd=0x{hwnd.ToInt64():X} workspace={kvp.Value} isWindow={isWindow} pinned={IsPinned(hwnd)} elevated={IsElevatedWindow(hwnd)} hiddenByUs={_hiddenByUs.Contains(hwnd)} pendingShow={_pendingShow.Contains(hwnd)} inLiveCollection={inLiveCollection} class=\"{className}\" title=\"{title}\"{flag}");
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

                ShowHiddenWindow(hwnd);
            }

            if (changed)
                WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public void ShowAllWindows()
        {
            // Used at shutdown: show everything unconditionally and clear tracking outright -
            // there's no taskbar left to care about the pending-show handoff.
            foreach (var hwnd in _hiddenByUs.ToList())
            {
                _hiddenByUs.Remove(hwnd);
                _pendingShow.Remove(hwnd);
                if (IsWindow(hwnd))
                    SetWindowVisible(hwnd, true);
            }
        }
    }
}
