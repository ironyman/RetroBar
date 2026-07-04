using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskList.xaml
    /// </summary>
    public partial class TaskList : UserControl
    {
        private bool isLoaded;
        private bool isScrollable;
        private double DefaultButtonWidth;
        private double MinButtonWidth;
        private double TaskButtonLeftMargin;
        private double TaskButtonRightMargin;
        private ICollectionView taskbarItems;
        private ObservableCollection<ApplicationWindow> rawSource;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register(nameof(ButtonWidth), typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        // The width every button is heading toward - equals ButtonWidth except while a shared
        // width animation is running, when ButtonWidth reads the eased in-between value. Slide-in
        // reveals animate toward this so they land on the settled width, not a mid-animation one.
        private double _targetButtonWidth;
        public double TargetButtonWidth => _targetButtonWidth > 0 ? _targetButtonWidth : ButtonWidth;

        public static DependencyProperty TasksProperty = DependencyProperty.Register(nameof(Tasks), typeof(Tasks), typeof(TaskList), new PropertyMetadata(TasksChangedCallback));

        public Tasks Tasks
        {
            get { return (Tasks)GetValue(TasksProperty); }
            set { SetValue(TasksProperty, value); }
        }

        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(Taskbar), typeof(TaskList), new PropertyMetadata(TasksChangedCallback));

        public Taskbar Host
        {
            get { return (Taskbar)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        public TaskList()
        {
            InitializeComponent();
            _groupManager = new TaskGroupManager(TasksList, Dispatcher);
        }

        private void SetStyles()
        {
            DefaultButtonWidth = Application.Current.FindResource("TaskButtonWidth") as double? ?? 0;
            MinButtonWidth = Application.Current.FindResource("TaskButtonMinWidth") as double? ?? 0;
            Thickness buttonMargin;

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                buttonMargin = Application.Current.FindResource("TaskButtonVerticalMargin") as Thickness? ?? new Thickness();
            }
            else
            {
                buttonMargin = Application.Current.FindResource("TaskButtonMargin") as Thickness? ?? new Thickness();
            }

            TaskButtonLeftMargin = buttonMargin.Left;
            TaskButtonRightMargin = buttonMargin.Right;
        }

        private void TaskList_OnLoaded(object sender, RoutedEventArgs e)
        {
            SetStyles();
            SetTasksCollection();
        }

        private void SetTasksCollection()
        {
            if (!isLoaded && Tasks != null && Host != null)
            {
                taskbarItems = Tasks.CreateGroupedWindowsCollection();
                if (taskbarItems != null)
                {
                    taskbarItems.CollectionChanged += GroupedWindows_CollectionChanged;
                    taskbarItems.Filter = Tasks_Filter;
                }

                TasksList.ItemsSource = taskbarItems;

                // A collapsed group's hidden members can still become the active window (e.g. via
                // Alt+Tab); track every window's State so the filter can be re-run and reveal it.
                rawSource = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
                if (rawSource != null)
                {
                    foreach (var w in rawSource)
                        w.PropertyChanged += Window_PropertyChangedForGroupCollapse;
                    rawSource.CollectionChanged += RawSource_CollectionChanged;
                }

                Settings.Instance.PropertyChanged += Settings_PropertyChanged;
                Host.hotkeyManager.TaskbarHotkeyPressed += TaskList_TaskbarHotkeyPressed;
                Host.hotkeyManager.CycleGroupWindowsHotkeyPressed += TaskList_CycleGroupWindowsHotkeyPressed;
                WorkspaceManager.Instance.WorkspaceSwitched += WorkspaceManager_WorkspaceSwitched;

                isLoaded = true;
            }
        }

        private void WorkspaceManager_WorkspaceSwitched(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke((Action)(() => taskbarItems?.Refresh()));
        }

        private static void TasksChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TaskList taskList && e.OldValue == null && e.NewValue != null)
            {
                taskList.SetTasksCollection();
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.MultiMonMode))
            {
                taskbarItems?.Refresh();
            }
            else if (e.PropertyName == nameof(Settings.ShowMultiMon))
            {
                if (Settings.Instance.MultiMonMode != MultiMonOption.AllTaskbars)
                {
                    taskbarItems?.Refresh();
                }
            }
        }
        private void TaskList_TaskbarHotkeyPressed(object sender, HotkeyManager.TaskbarHotkeyEventArgs e)
        {
            if (Settings.Instance.WinNumHotkeysAction == WinNumHotkeysOption.SwitchTasks && Host.Screen.Primary)
            {
                try
                {
                    bool exists = taskbarItems.MoveCurrentToPosition(e.index);

                    if (exists)
                    {
                        ApplicationWindow window = taskbarItems.CurrentItem as ApplicationWindow;

                        if (e.isShiftPressed)
                        {
                            // Open new instance when Shift is pressed
                            ShellHelper.StartProcess(window.IsUWP ? "appx:" + window.AppUserModelID : window.WinFileName);
                        }
                        else
                        {
                            // Normal behavior - switch to existing window
                            if (window.State == ApplicationWindow.WindowState.Active && window.CanMinimize)
                            {
                                window.Minimize();
                            }
                            else
                            {
                                window.BringToFront();
                            }
                        }
                    }

                }
                catch (ArgumentOutOfRangeException) { }
            }
        }

        // Alt+` cycles focus to the next window in the currently active window's task group. If
        // the active window isn't part of a tiled group, fall back to cycling through all
        // on-screen, non-occluded windows in clockwise order instead.
        private void TaskList_CycleGroupWindowsHotkeyPressed(object sender, EventArgs e)
        {
            var activeWindow = rawSource?.FirstOrDefault(w => w.State == ApplicationWindow.WindowState.Active);
            if (activeWindow == null) return;

            var group = _groupManager.GetGroupForWindow(activeWindow);
            if (group != null && group.Windows.Count > 1)
            {
                var ordered = _groupManager.GetGroupWindowsOrdered(activeWindow, rawSource);
                int index = ordered.IndexOf(activeWindow);
                if (index >= 0)
                {
                    ordered[(index + 1) % ordered.Count].BringToFront();
                    return;
                }
            }

            CycleNonOccludedWindowsClockwise(activeWindow);
        }

        // Cycles focus among all currently visible, non-occluded windows (across the whole
        // desktop, not just this taskbar's collection), moving to whichever candidate is next in
        // clockwise order around the group's center point.
        private void CycleNonOccludedWindowsClockwise(ApplicationWindow activeWindow)
        {
            if (rawSource == null) return;

            ShellLogger.Debug($"CycleGroup: active={DescribeWindow(activeWindow.Handle, activeWindow.Title)}");

            var candidates = new List<(ApplicationWindow window, Point center)>();
            foreach (var w in rawSource)
            {
                if (!w.ShowInTaskbar || w.IsMinimized)
                {
                    ShellLogger.Debug($"CycleGroup:   skip (not shown/minimized) {DescribeWindow(w.Handle, w.Title)}");
                    continue;
                }

                TryGetVisibleRect(w.Handle, out var rect);
                bool occluded = IsOccluded(w.Handle);
                ShellLogger.Debug($"CycleGroup:   {(occluded ? "OCCLUDED" : "candidate")} {DescribeWindow(w.Handle, w.Title)} rect=[{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}]");

                if (!occluded)
                    candidates.Add((w, new Point((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0)));
            }

            if (candidates.Count < 2)
            {
                ShellLogger.Debug($"CycleGroup: only {candidates.Count} candidate(s), nothing to cycle to");
                return;
            }

            double centroidX = candidates.Average(x => x.center.X);
            double centroidY = candidates.Average(x => x.center.Y);
            ShellLogger.Debug($"CycleGroup: centroid=({centroidX:F0},{centroidY:F0})");

            // Clockwise order: computes the centroid of all candidate
            // window centers, then sorts candidates by angle (atan2)
            // around that centroid — this produces a clockwise sweep
            // in normal screen coordinates.

            var orderedClockwise = candidates
                .Select(x => (x.window, x.center, angle: NormalizeAngle(Math.Atan2(x.center.Y - centroidY, x.center.X - centroidX))))
                .OrderBy(x => x.angle)
                .ToList();

            foreach (var x in orderedClockwise)
                ShellLogger.Debug($"CycleGroup:   ordered angle={x.angle * 180 / Math.PI:F0}deg center=({x.center.X:F0},{x.center.Y:F0}) {DescribeWindow(x.window.Handle, x.window.Title)}");

            var ordered = orderedClockwise.Select(x => x.window).ToList();
            int index = ordered.IndexOf(activeWindow);
            var next = index >= 0
                ? ordered[(index + 1) % ordered.Count]
                : ordered[0];

            ShellLogger.Debug($"CycleGroup: next={DescribeWindow(next.Handle, next.Title)}");

            if (next != activeWindow)
                next.BringToFront();
        }

        private static string DescribeWindow(IntPtr handle, string title) => $"'{title}' (0x{handle:X})";

        // A window is occluded if the windows stacked above it in Z order (visible, non-minimized,
        // non-cloaked) jointly cover its entire rect - not necessarily any single one of them. Two
        // windows Aero-snapped side by side, for example, can together fully hide a window behind
        // them without either individually containing it, so this accumulates a region rather than
        // checking single-window containment. Walking GW_HWNDPREV from a window steps through the
        // windows stacked above it (toward the top of the Z order).
        private static bool IsOccluded(IntPtr handle)
        {
            if (!TryGetVisibleRect(handle, out var rect)) return true;

            using var region = new System.Drawing.Region(ToRectangle(rect));
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);

            IntPtr current = NativeMethods.GetWindow(handle, NativeMethods.GetWindow_Cmd.GW_HWNDPREV);
            while (current != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(current) && !NativeMethods.IsIconic(current) && !IsCloaked(current)
                    && TryGetVisibleRect(current, out var otherRect))
                {
                    region.Exclude(ToRectangle(otherRect));
                    if (region.IsEmpty(g))
                        return true;
                }
                current = NativeMethods.GetWindow(current, NativeMethods.GetWindow_Cmd.GW_HWNDPREV);
            }
            return false;
        }

        private static System.Drawing.Rectangle ToRectangle(NativeMethods.Rect rect)
            => System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

        // GetWindowRect includes the invisible resize-border/shadow margin that Windows 10/11
        // pads around top-level windows, which can make adjacent Aero-snapped windows appear to
        // overlap (or a window appear larger than it visually is) even though nothing is actually
        // drawn there. DWMWA_EXTENDED_FRAME_BOUNDS reports the true on-screen visual bounds, so
        // prefer it and only fall back to GetWindowRect where DWM composition isn't available.
        private static bool TryGetVisibleRect(IntPtr handle, out NativeMethods.Rect rect)
        {
            if (EnvironmentHelper.IsWindows8OrBetter)
            {
                int cbSize = Marshal.SizeOf(typeof(NativeMethods.Rect));
                if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, cbSize) == 0)
                    return true;
            }

            return NativeMethods.GetWindowRect(handle, out rect);
        }

        // Cloaked windows (e.g. UWP apps on another virtual desktop, or suspended) still report
        // IsWindowVisible() == true, so they must be filtered out separately or they show up as
        // full-screen "occluders" that were never actually drawn on top of anything.
        private static bool IsCloaked(IntPtr handle)
        {
            if (!EnvironmentHelper.IsWindows8OrBetter) return false;

            int cbSize = Marshal.SizeOf(typeof(uint));
            return NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, out uint cloaked, cbSize) == 0 && cloaked > 0;
        }

        private static double NormalizeAngle(double angle) => angle < 0 ? angle + 2 * Math.PI : angle;

        private bool Tasks_Filter(object obj)
        {
            if (obj is ApplicationWindow window)
            {
                if (!window.ShowInTaskbar)
                {
                    return false;
                }

                if (WorkspaceManager.Instance.IsHiddenByUs(window.Handle))
                {
                    return false;
                }

                // A collapsed group shows only its leftmost member; the rest are filtered out -
                // unless one of them is the active window, in which case collapsing is suspended
                // (hiding the button the user is actively using wouldn't make sense).
                var group = _groupManager.GetGroupForWindow(window);
                if (group != null && group.IsCollapsed && !_groupManager.GroupHasActiveWindow(group))
                {
                    var source = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
                    if (!ReferenceEquals(_groupManager.GetCollapsedRepresentative(group, source), window))
                    {
                        return false;
                    }
                }

                if (!Settings.Instance.ShowMultiMon || Settings.Instance.MultiMonMode == MultiMonOption.AllTaskbars)
                {
                    return true;
                }

                if (Settings.Instance.MultiMonMode == MultiMonOption.SameAsWindowAndPrimary && Host.Screen.Primary)
                {
                    return true;
                }

                IntPtr hMonitor = window.HMonitor;
                if (Host.Screen.Primary && !Host.windowManager.IsValidHMonitor(hMonitor))
                {
                    return true;
                }

                if (hMonitor != Host.Screen.HMonitor)
                {
                    return false;
                }
            }

            return true;
        }

        private void TaskList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (taskbarItems != null)
            {
                taskbarItems.CollectionChanged -= GroupedWindows_CollectionChanged;
                taskbarItems.Filter = null;
            }

            if (rawSource != null)
            {
                foreach (var w in rawSource)
                    w.PropertyChanged -= Window_PropertyChangedForGroupCollapse;
                rawSource.CollectionChanged -= RawSource_CollectionChanged;
                rawSource = null;
            }

            if (Host != null)
            {
                Host.hotkeyManager.TaskbarHotkeyPressed -= TaskList_TaskbarHotkeyPressed;
                Host.hotkeyManager.CycleGroupWindowsHotkeyPressed -= TaskList_CycleGroupWindowsHotkeyPressed;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            WorkspaceManager.Instance.WorkspaceSwitched -= WorkspaceManager_WorkspaceSwitched;

            isLoaded = false;
        }

        // Keeps the PropertyChanged subscription (used to notice a collapsed group's hidden
        // member becoming active) in sync as windows are added to/removed from the taskbar.
        private void RawSource_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ApplicationWindow w in e.OldItems)
                    w.PropertyChanged -= Window_PropertyChangedForGroupCollapse;
            }

            if (e.NewItems != null)
            {
                foreach (ApplicationWindow w in e.NewItems)
                    w.PropertyChanged += Window_PropertyChangedForGroupCollapse;
            }

            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && rawSource != null)
            {
                foreach (var w in rawSource)
                    w.PropertyChanged += Window_PropertyChangedForGroupCollapse;
            }
        }

        // A collapsed group only hides its non-representative members while none of them is the
        // active window - re-run the filter whenever a grouped window's active state changes so a
        // newly-activated hidden member is revealed (sliding in), and re-hidden once it's no
        // longer active (sliding out).
        //
        // TasksService.WINDOWACTIVATED sets the previously-active window to Inactive and the
        // newly-active one to Active as two separate, synchronous property sets (old-Inactive
        // first). Switching focus between two windows already inside the same expanded group
        // therefore raises this handler twice in a row: on the first (deactivate) call,
        // GroupHasActiveWindow briefly reads false even though the group is about to have a new
        // active member and shouldn't collapse at all. Deferring the hide decision to just after
        // the current dispatch lets the second (activate) call - which runs synchronously right
        // after, within the same message handling - land first and correct that reading.
        private void Window_PropertyChangedForGroupCollapse(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ApplicationWindow.State) || sender is not ApplicationWindow window)
                return;

            var group = _groupManager.GetGroupForWindow(window);
            if (group == null || !group.IsCollapsed)
                return;

            var source = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
            var representative = _groupManager.GetCollapsedRepresentative(group, source);
            var others = group.Windows.Where(w => !ReferenceEquals(w, representative)).ToList();
            if (others.Count == 0) return;

            // All non-representative members share the same visibility, so any one of them stands
            // in for "is the group currently expanded".
            bool currentlyVisible = TasksList.ItemContainerGenerator.ContainerFromItem(others[0]) != null;

            if (_groupManager.GroupHasActiveWindow(group))
            {
                if (currentlyVisible) return; // already expanded - a same-group focus swap, not a reveal
                MarkForReveal(others);
                taskbarItems?.Refresh();
            }
            else if (currentlyVisible)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
                {
                    // Re-check: a same-group focus swap's matching activate call may have already
                    // landed, in which case the group never actually lost its last active window.
                    if (_groupManager.GroupHasActiveWindow(group)) return;
                    AnimateHide(others, () => taskbarItems?.Refresh());
                }));
            }
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            var action = e.Action;

            // Width changes caused by buttons appearing/disappearing ease into place so the whole
            // row shrinks or grows as one motion (e.g. uncollapsing a group) instead of snapping.
            // A Move can't change how many buttons are visible, so it never needs a width
            // recompute - and reordering a collapsed group's members (e.g. committing a group
            // drag) fires a burst of individual Move notifications where the members are
            // momentarily non-contiguous, which can make the collapsed-group filter's "leftmost
            // member is the representative" pick transiently flip. Recomputing width on those
            // transient counts is what produced a spurious shrink/grow flash on every group drag.
            if (action != System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            {
                SetTaskButtonWidth(animate: true);
            }

            // When a new window is inserted after its active parent (GroupAfterParent setting),
            // make sure it doesn't land in the middle of the parent's task group — if it lands
            // anywhere inside a group's span, push it to right after that group's last member.
            if (action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                if (e.NewItems != null)
                {
                    var newWindows = e.NewItems.OfType<ApplicationWindow>().ToList();
                    // Deferred so this runs after WPF's own ItemsControl has finished reacting to
                    // the Add notification — moving the collection again synchronously from inside
                    // this handler races the container generator and can leave the new button's
                    // container never generated (the button silently doesn't appear).
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => FixupGroupSplitInsertions(newWindows)));
                }
                return;
            }

            // ObservableCollection.Move raises CollectionChanged with Action=Move and OldItems set,
            // but the window is not gone — only clean up groups for actual removals.
            HandleGroupedWindowsRemovalOrReset(e);
        }

        // Relocates any newly added window that landed strictly inside an existing task group's
        // span (by index) to right after that group's last member, so a fresh window can never
        // visually split a group apart no matter how it was inserted.
        private void FixupGroupSplitInsertions(List<ApplicationWindow> newWindows)
        {
            if (taskbarItems?.SourceCollection is not ObservableCollection<ApplicationWindow> source)
                return;

            foreach (var newWindow in newWindows)
            {
                int insertedIdx = source.IndexOf(newWindow);
                if (insertedIdx < 0) continue;

                foreach (var group in _groupManager.Groups)
                {
                    if (group.Windows.Count < 2) continue;

                    int minIdx = int.MaxValue, maxIdx = -1;
                    foreach (var w in group.Windows)
                    {
                        int idx = source.IndexOf(w);
                        if (idx < 0) continue;
                        if (idx < minIdx) minIdx = idx;
                        if (idx > maxIdx) maxIdx = idx;
                    }

                    if (maxIdx < 0 || insertedIdx <= minIdx || insertedIdx >= maxIdx) continue;

                    // Landed strictly inside the group's span — move it to right after the group.
                    // maxIdx is already the correct target: removing the new window from
                    // insertedIdx (< maxIdx) shifts maxIdx down by one, and inserting "after" adds
                    // one back.
                    source.Move(insertedIdx, maxIdx);
                    break;
                }
            }
        }

        private void HandleGroupedWindowsRemovalOrReset(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            var action = e.Action;
            if (action != System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace &&
                action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                return;

            bool changed = false;
            if (e.OldItems != null)
            {
                // e.OldItems includes windows that merely dropped out of the *filtered* view -
                // cloaked by a native virtual-desktop switch, hidden by our own WorkspaceManager,
                // or excluded by the multi-monitor filter - as well as windows that are genuinely
                // gone. Only genuinely-gone windows should lose their group membership; the rest
                // still exist and will resurface (with their group intact) once unfiltered.
                var rawSource = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
                var stillExists = new HashSet<IntPtr>();
                if (rawSource != null)
                {
                    foreach (var w in rawSource)
                        stillExists.Add(w.Handle);
                }

                var oldWindows = e.OldItems.OfType<ApplicationWindow>().ToList();

                var genuinelyGone = oldWindows
                    .Where(w => !stillExists.Contains(w.Handle) && !WorkspaceManager.Instance.IsHiddenByUs(w.Handle))
                    .ToList();

                if (genuinelyGone.Count > 0)
                {
                    changed = _groupManager.RemoveWindows(genuinelyGone);
                }

                // A grouped window that's still around but cloaked (not merely monitor-filtered)
                // may have been moved to a different virtual desktop individually rather than as
                // part of a bulk switch taking its whole group along. Check once things settle.
                bool anyCloakedGroupMember = oldWindows.Any(w =>
                    stillExists.Contains(w.Handle) && !w.ShowInTaskbar && _groupManager.GetGroupForWindow(w) != null);

                if (anyCloakedGroupMember && rawSource != null)
                {
                    _groupManager.ScheduleDesktopSplitCheck(rawSource);
                }
            }
            else if (action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                // Full reset — dissolve all groups whose members are no longer in the source.
                // Windows hidden by WorkspaceManager (another workspace) are genuinely absent
                // from the source collection right now but aren't gone - keep their membership.
                if (taskbarItems?.SourceCollection is System.Collections.IEnumerable src)
                {
                    changed = _groupManager.ReconcileWithSource(src.OfType<ApplicationWindow>(), WorkspaceManager.Instance.IsHiddenByUs);
                }
            }

            if (changed)
            {
                _groupManager.UpdateGroupVisuals();
                taskbarItems?.Refresh();
            }
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ShellLogger.Debug($"TaskList: SizeChanged oldSize={e.PreviousSize.Width:F3}x{e.PreviousSize.Height:F3} newSize={e.NewSize.Width:F3}x{e.NewSize.Height:F3} TasksList.ActualWidth={TasksList.ActualWidth:F3}");
            SetTaskButtonWidth();
        }

        private void SetTaskButtonWidth(bool animate = false)
        {
            if (Host is null)
                return; // The state is trashed, but presumably it's just a transition

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                ApplyButtonWidth(ActualWidth, false);
                SetScrollable(true); // while technically not always scrollable, we don't run into DPI-specific issues with it enabled while vertical
                return;
            }

            double height = ActualHeight;
            int rows = Host.Rows;

            int taskCount = TasksList.Items.Count;
            double margin = TaskButtonLeftMargin + TaskButtonRightMargin;
            double availableWidth = TasksList.ActualWidth;
            double maxWidth = availableWidth / Math.Ceiling((double)taskCount / rows);
            double defaultWidth = DefaultButtonWidth + margin;
            double minWidth = MinButtonWidth + margin;

            double newButtonWidth;
            if (maxWidth > defaultWidth)
            {
                newButtonWidth = defaultWidth;
                SetScrollable(false);
            }
            else if (maxWidth < minWidth)
            {
                newButtonWidth = Math.Ceiling(defaultWidth / 2);
                SetScrollable(true);
            }
            else
            {
                double dpiScale = Host.DpiScale;
                double perButtonPhysical = Math.Floor(availableWidth * dpiScale / taskCount);
                newButtonWidth = perButtonPhysical / dpiScale;
                SetScrollable(false);
            }

            ShellLogger.Debug($"TaskList: SetTaskButtonWidth taskCount={taskCount} rows={rows} availableWidth={availableWidth:F3} maxWidth={maxWidth:F3} defaultWidth={defaultWidth} newButtonWidth={newButtonWidth:F3} totalWidth={taskCount * newButtonWidth:F3} overflow={taskCount * newButtonWidth > availableWidth} dpiScale={Host.DpiScale}");
            ApplyButtonWidth(newButtonWidth, animate);

            // Post-layout check: confirm actual layout after WPF processes the width change
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                var wrapPanel = FindItemsPanel<WrapPanel>(TasksList);
                double wrapHeight = wrapPanel?.ActualHeight ?? -1;
                double wrapWidth = wrapPanel?.ActualWidth ?? -1;
                ShellLogger.Debug($"TaskList: Post-layout TasksList.ActualWidth={TasksList.ActualWidth:F3} ButtonWidth={ButtonWidth:F3} itemCount={TasksList.Items.Count} wrapPanel={wrapWidth:F3}x{wrapHeight:F3} taskbarHeight={ActualHeight:F3} wrapping={wrapHeight > ActualHeight + 1}");
            }));
        }

        // Applies a new shared button width, optionally easing every button from the current width
        // to the new one in unison instead of snapping - reflows caused by buttons appearing or
        // disappearing (a group collapsing/uncollapsing, a window opening/closing) read much
        // smoother animated, whereas taskbar resizes should track the mouse instantly. The local
        // (unanimated) value is always the target, so anything reading ButtonWidth after the
        // animation stops sees the settled width.
        private void ApplyButtonWidth(double newWidth, bool animate)
        {
            double oldWidth = ButtonWidth; // reads the eased value if an animation is in flight
            _targetButtonWidth = newWidth;

            if (!animate || !Settings.Instance.AnimateTaskbarLayout || oldWidth <= 0 || Math.Abs(newWidth - oldWidth) < 0.5)
            {
                BeginAnimation(ButtonWidthProperty, null);
                ButtonWidth = newWidth;
                return;
            }

            ButtonWidth = newWidth;
            var animation = new DoubleAnimation(oldWidth, newWidth, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            BeginAnimation(ButtonWidthProperty, animation);
        }

        private void SetScrollable(bool canScroll)
        {
            if (canScroll == isScrollable) return;

            if (canScroll)
            {
                TasksScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                TasksScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }

            isScrollable = canScroll;
        }

        private void TasksScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!isScrollable)
            {
                e.Handled = true;
            }
        }

        private static T FindItemsPanel<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var found = FindItemsPanel<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        #region Task groups

        // All task-group state and mutation logic is owned by _groupManager; this region only
        // wires TaskList's own drag mechanics into it.
        private readonly TaskGroupManager _groupManager;

        // Windows whose next TaskButton.Loaded should slide in (width 0 -> full) regardless of
        // the general SlideTaskbarButtons setting - set right before a Refresh that reveals a
        // window previously hidden by a collapsed group, so TaskButton_OnLoaded can consume it.
        private readonly HashSet<ApplicationWindow> _pendingReveal = new HashSet<ApplicationWindow>();

        // Called by TaskButton.Loaded so new buttons pick up their group color.
        public void RefreshGroupVisual(TaskButton btn) => _groupManager.RefreshGroupVisual(btn);

        // Called by TaskButton.Loaded to check (and clear) whether this window's button should
        // force a slide-in animation instead of following the general SlideTaskbarButtons setting.
        public bool ConsumeRevealAnimation(ApplicationWindow window)
            => window != null && _pendingReveal.Remove(window);

        // The set of windows currently hidden because they're a non-representative member of a
        // collapsed group - mirrors the condition in Tasks_Filter. Used to detect windows that
        // transition from hidden to visible around a group operation, so those reveals can slide in.
        private HashSet<ApplicationWindow> GetHiddenGroupMembers()
        {
            var hidden = new HashSet<ApplicationWindow>();
            var source = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
            if (source == null) return hidden;

            foreach (var window in source)
            {
                var group = _groupManager.GetGroupForWindow(window);
                if (group == null || !group.IsCollapsed || _groupManager.GroupHasActiveWindow(group))
                    continue;

                var representative = _groupManager.GetCollapsedRepresentative(group, source);
                if (!ReferenceEquals(window, representative))
                    hidden.Add(window);
            }

            return hidden;
        }

        // Marks windows so their next Loaded slides in, then clears any leftovers once layout has
        // settled (anything actually revealed will have already consumed its entry by then).
        private void MarkForReveal(IEnumerable<ApplicationWindow> windows)
        {
            bool any = false;
            foreach (var w in windows)
            {
                _pendingReveal.Add(w);
                any = true;
            }

            if (any)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => _pendingReveal.Clear()));
            }
        }

        // Called from TaskButton right-click → Remove from group. Removing the visible
        // representative of a still-collapsed group can both reveal a new representative and
        // leave the old one behind as a standalone (still-visible) button, so diff hidden-member
        // state before/after rather than assuming only reveals are possible. Ungrouping also
        // repositions the window next to its old group (TaskGroupManager.UngroupWindow moves it in
        // the source collection), so slide every button whose slot shifted from its old position
        // to its new one instead of letting the reflow snap instantly.
        public void UngroupWindow(ApplicationWindow window)
        {
            var hiddenBefore = GetHiddenGroupMembers();
            var positionsBefore = CaptureButtonPositions();

            _groupManager.UngroupWindow(window, taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>);

            var hiddenAfter = GetHiddenGroupMembers();
            var revealed = hiddenBefore.Where(w => !hiddenAfter.Contains(w)).ToList();
            var newlyHidden = hiddenAfter.Where(w => !hiddenBefore.Contains(w)).ToList();

            AnimateHide(newlyHidden, () =>
            {
                MarkForReveal(revealed);
                taskbarItems?.Refresh();
                AnimateLayoutChanges(positionsBefore);
            });
        }

        // Snapshots every currently-rendered button's position relative to the WrapPanel, keyed by
        // window - the "First" half of a FLIP reposition animation.
        private Dictionary<ApplicationWindow, Point> CaptureButtonPositions()
        {
            var positions = new Dictionary<ApplicationWindow, Point>();
            var panel = FindItemsPanel<WrapPanel>(TasksList);
            if (panel == null) return positions;

            for (int i = 0; i < TasksList.Items.Count; i++)
            {
                if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
                if (cp.DataContext is not ApplicationWindow w) continue;
                positions[w] = cp.TranslatePoint(new Point(0, 0), panel);
            }

            return positions;
        }

        // The "Last, Invert, Play" half of a FLIP reposition animation: once the layout has
        // settled after a mutation, any button whose slot moved from its position in oldPositions
        // is snapped back to its old spot with a translate transform and animated to (0,0) - i.e.
        // it visibly slides from where it was to where it now belongs. Containers may have been
        // entirely recreated by a Refresh() in between (a full container Reset), so this re-looks
        // up each window's current container rather than reusing the ones captured earlier.
        private void AnimateLayoutChanges(Dictionary<ApplicationWindow, Point> oldPositions)
        {
            if (oldPositions.Count == 0 || !Settings.Instance.AnimateTaskbarLayout) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                var panel = FindItemsPanel<WrapPanel>(TasksList);
                if (panel == null) return;

                for (int i = 0; i < TasksList.Items.Count; i++)
                {
                    if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
                    if (cp.DataContext is not ApplicationWindow w) continue;
                    if (!oldPositions.TryGetValue(w, out Point oldPos)) continue;

                    Point newPos = cp.TranslatePoint(new Point(0, 0), panel);
                    Vector delta = oldPos - newPos;
                    if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) continue;

                    cp.RenderTransform = null;
                    var transform = new TranslateTransform(delta.X, delta.Y);
                    cp.RenderTransform = transform;

                    var ease = new SineEase { EasingMode = EasingMode.EaseOut };
                    transform.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                    transform.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                }
            }));
        }

        // Called from TaskButton right-click → New color for group.
        public void ChangeGroupColor(ApplicationWindow window) => _groupManager.ChangeGroupColor(window);

        // Called from TaskButton right-click → Remove group.
        public void RemoveGroup(ApplicationWindow window)
        {
            _groupManager.RemoveGroup(window);
            taskbarItems?.Refresh();
        }

        // Returns the windows belonging to the same group as the given window.
        public List<ApplicationWindow> GetGroupWindows(ApplicationWindow window) => _groupManager.GetGroupWindows(window);

        // Returns the TaskGroup for the given window, or null if ungrouped.
        public TaskGroup GetGroupForWindow(ApplicationWindow window) => _groupManager.GetGroupForWindow(window);

        // Called from TaskButton right-click -> Tile group.
        public async void TileGroup(ApplicationWindow window)
        {
            var windows = _groupManager.GetGroupWindowsOrdered(window, taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>);
            var group = _groupManager.GetGroupForWindow(window);
            await WindowTiler.TileGroupAsync(windows);
            if (group != null && group.Windows.Count >= 2 && group.Windows.Count <= 4)
                group.SetTiledRects(windows);
        }

        // Collapses the group containing the given window, sliding out the members that will
        // become hidden before actually applying the filter change.
        public void CollapseGroup(ApplicationWindow window)
        {
            var group = _groupManager.GetGroupForWindow(window);
            if (group == null || group.IsCollapsed) return;

            AnimateGroupsCollapsing(new List<TaskGroup> { group }, () =>
            {
                _groupManager.CollapseGroup(window);
                taskbarItems?.Refresh();
            });
        }

        // Collapses all task groups, sliding out every member that will become hidden.
        public void CollapseAllGroups()
        {
            var groups = _groupManager.Groups.Where(g => !g.IsCollapsed).ToList();
            if (groups.Count == 0) return;

            AnimateGroupsCollapsing(groups, () =>
            {
                _groupManager.CollapseAllGroups();
                taskbarItems?.Refresh();
            });
        }

        // Uncollapses all task groups, sliding in every member that becomes visible.
        public void UncollapseAllGroups()
        {
            var hiddenBefore = GetHiddenGroupMembers();
            _groupManager.UncollapseAllGroups();
            MarkForReveal(hiddenBefore);
            taskbarItems?.Refresh();
        }

        // Slides out the soon-to-be-hidden members of the given (not-yet-collapsed) groups, then
        // invokes onComplete once every animation has finished (or immediately if there was
        // nothing to animate - e.g. every group's collapse is currently suspended by an active
        // window).
        private void AnimateGroupsCollapsing(List<TaskGroup> groups, Action onComplete)
        {
            var source = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
            var toHide = new List<ApplicationWindow>();

            if (source != null)
            {
                foreach (var group in groups)
                {
                    if (_groupManager.GroupHasActiveWindow(group)) continue;

                    var representative = _groupManager.GetCollapsedRepresentative(group, source);
                    toHide.AddRange(group.Windows.Where(w => !ReferenceEquals(w, representative)));
                }
            }

            AnimateHide(toHide, onComplete);
        }

        // Slides each of the given windows' currently-rendered buttons out to width 0, then
        // invokes onComplete once every animation has finished (or immediately if there was
        // nothing to animate - e.g. a window's button isn't currently rendered, or the taskbar is
        // vertical, where a width slide wouldn't read as a slide).
        private void AnimateHide(IEnumerable<ApplicationWindow> windows, Action onComplete)
        {
            bool horizontal = Settings.Instance.AnimateTaskbarLayout && Host?.Orientation != Orientation.Vertical;
            var buttons = new List<TaskButton>();

            if (horizontal)
            {
                foreach (var w in windows)
                {
                    if (TasksList.ItemContainerGenerator.ContainerFromItem(w) is ContentPresenter cp &&
                        TaskGroupManager.GetTaskButton(cp) is TaskButton btn)
                    {
                        buttons.Add(btn);
                    }
                }
            }

            if (buttons.Count == 0)
            {
                onComplete();
                return;
            }

            int remaining = buttons.Count;
            foreach (var btn in buttons)
            {
                btn.AnimateSlideOut(() =>
                {
                    if (--remaining <= 0)
                        onComplete();
                });
            }
        }

        private void StartGroupHover(int targetIndex) => _groupManager.StartHover(targetIndex, OnGroupHoverConfirmed);

        private void StartSoloRejoinHover(int targetIndex) => _groupManager.StartHover(targetIndex, OnSoloRejoinConfirmed);

        private void CancelGroupHover() => _groupManager.CancelHover();

        private void OnGroupHoverConfirmed()
        {
            if (!_isDragging || _groupManager.HoverTargetIndex < 0 || _dragContainer == null) return;

            var draggedWindow = _dragContainer.DataContext as ApplicationWindow;
            if (_groupManager.HoverTargetIndex >= _dragContainers.Count) return;
            var targetWindow = _dragContainers[_groupManager.HoverTargetIndex].DataContext as ApplicationWindow;
            if (draggedWindow == null || targetWindow == null) return;

            _groupManager.ConfirmHover(draggedWindow, targetWindow);
        }

        // The dragged button dwelled long enough over one of its own former groupmates - it
        // rejoins the group it started in. Membership was never actually changed mid-drag, so
        // this just flips the live "still in group" flag and its visual back on.
        private void OnSoloRejoinConfirmed()
        {
            if (!_isDragging || !_dragIsSolo || _dragSoloOriginalGroup == null || _dragContainer == null) return;

            _dragSoloInGroup = true;
            _groupManager.SetSoloDragVisual(_dragContainer, _dragSoloOriginalGroup, true);
        }

        // Computes the dragged button's bounding rect in WrapPanel coordinates,
        // accounting for the cursor offset along the drag axis.
        private Rect GetDraggedRect(Vector delta, bool horizontal)
        {
            var s = _dragSlots[_dragFromIndex];
            var sz = _dragSizes[_dragFromIndex];
            return new Rect(
                s.X + (horizontal ? delta.X : 0),
                s.Y + (horizontal ? 0 : delta.Y),
                sz.Width, sz.Height);
        }

        // Bounding rect for a sibling button, including any active sibling animation offset.
        private Rect GetSlotRect(int idx)
        {
            var s = _dragSlots[idx];
            var sz = _dragSizes[idx];
            double ox = 0, oy = 0;
            if (_dragSiblingTargets.TryGetValue(_dragContainers[idx], out Point target))
            {
                ox = target.X;
                oy = target.Y;
            }
            return new Rect(s.X + ox, s.Y + oy, sz.Width, sz.Height);
        }

        // Returns the fraction of the dragged button's main-axis dimension that overlaps `other`.
        // At 50% the drag-swap fires; 25% is the threshold for starting the group-hover timer.
        private static float OverlapFraction(Rect dragged, Rect other, bool horizontal)
        {
            double dim = horizontal ? dragged.Width : dragged.Height;
            if (dim <= 0) return 0f;
            double overlap = horizontal
                ? Math.Max(0, Math.Min(dragged.Right, other.Right) - Math.Max(dragged.Left, other.Left))
                : Math.Max(0, Math.Min(dragged.Bottom, other.Bottom) - Math.Max(dragged.Top, other.Top));
            return (float)(overlap / dim);
        }

        // Whether a solo-dragged button still counts as part of _dragSoloOriginalGroup, given its
        // current block gap. Strictly nested between two former groupmates is unambiguously in;
        // having swapped a full block past the group is unambiguously out. Sitting right at the
        // group's edge (gap touches the group with nothing in between) is ambiguous, so it only
        // flips to "out" once the dragged button has moved past that edge by a quarter of its own width.
        private bool IsSoloStillWithinGroup(int gap, Vector delta, bool horizontal)
        {
            if (gap > _dragSoloGroupBlockFirst && gap < _dragSoloGroupBlockLast + 1)
                return true;

            if (gap < _dragSoloGroupBlockFirst || gap > _dragSoloGroupBlockLast + 1)
                return false;

            var draggedRect = GetDraggedRect(delta, horizontal);
            double buttonWidth = horizontal ? draggedRect.Width : draggedRect.Height;

            if (gap == _dragSoloGroupBlockFirst)
            {
                var firstRect = GetSlotRect(_dragSoloGroupFirstItemIdx);
                double edge = horizontal ? firstRect.Left : firstRect.Top;
                double draggedTrailing = horizontal ? draggedRect.Right : draggedRect.Bottom;
                return edge - draggedTrailing <= buttonWidth / 4.0;
            }
            else
            {
                var lastRect = GetSlotRect(_dragSoloGroupLastItemIdx);
                double edge = horizontal ? lastRect.Right : lastRect.Bottom;
                double draggedLeading = horizontal ? draggedRect.Left : draggedRect.Top;
                return draggedLeading - edge <= buttonWidth / 4.0;
            }
        }

        #endregion

        #region Live drag reorder

        // Windows 10-style live reordering: the dragged button tracks the cursor while the
        // remaining buttons slide out of the way with fluid animations and the dragged button
        // snaps into place on release.

        private const double DragAnimationMs = 180;
        private const double DragSnapMs = 200;

        private bool _isDragging;
        private ContentPresenter _dragContainer;
        private WrapPanel _dragPanel;
        private Point _dragStartPanelPoint;
        private List<ContentPresenter> _dragContainers;
        private List<Point> _dragSlots;
        private List<Size> _dragSizes;
        private int _dragFromIndex;
        private int _dragToIndex;
        private readonly Dictionary<ContentPresenter, Point> _dragSiblingTargets = new();

        // Non-primary group members that travel with the dragged button.
        private List<int> _dragGroupMemberIndices = new List<int>();
        // Group members sorted by their original slot position (primary included).
        private List<int> _dragGroupSorted = new List<int>();
        // Index of the primary button within _dragGroupSorted.
        private int _dragGroupPrimaryOffset;

        // Ctrl+drag: move only the pressed button, leaving its groupmates in place, so it can be
        // reordered within the group or pulled out of it entirely.
        private bool _dragIsSolo;
        // The group the dragged (primary) button belonged to when the drag started, if any -
        // regardless of solo vs group drag. Used to forbid merge-hover for an already-grouped
        // button even when its groupmates aren't currently rendered (so groups never merge into
        // other groups; only ungrouped buttons may join a group).
        private TaskGroup _dragOriginalGroup;
        // The group the solo-dragged button belonged to when the drag started (null if ungrouped).
        private TaskGroup _dragSoloOriginalGroup;
        // Block-list index range (inclusive) occupied by _dragSoloOriginalGroup's remaining
        // members; -1 if there is no such group. Used to detect the dragged button crossing out
        // of its own group's span.
        private int _dragSoloGroupBlockFirst = -1;
        private int _dragSoloGroupBlockLast = -1;
        // Original container indices (into _dragContainers/_dragSlots) of the first and last
        // remaining members of _dragSoloOriginalGroup, in visual order; -1 if no such group.
        private int _dragSoloGroupFirstItemIdx = -1;
        private int _dragSoloGroupLastItemIdx = -1;
        // Whether the solo-dragged button currently counts as still belonging to its original
        // group. Leaving fires once the dragged button, sitting right at the group's edge, has
        // moved a quarter of its own width past that edge; re-entering requires the same
        // dwell-to-confirm hover gesture as an ordinary group merge, restricted to only that
        // original group - this asymmetry is the intended hysteresis between leaving and rejoining.
        private bool _dragSoloInGroup;

        // A hover-merge was confirmed on this drag, so the grouped collection needs a Refresh to
        // re-run its filter (e.g. when merging into a collapsed group). Deferred until the very end
        // of CommitDrag: refreshing mid-drag raises a Reset that regenerates every container,
        // detaching the dragged button's container from the snap animation and letting the freshly
        // generated one reappear at its old slot — the button visibly snaps back to its origin.
        private bool _dragCommittedMerge;

        // The non-moving buttons, partitioned into "blocks": a run of members belonging to the
        // same foreign task group is one indivisible block, everything else is its own singleton
        // block. Swaps are evaluated a whole block at a time so a multi-button group is treated
        // like one wide button rather than being crossed — and split apart — one member at a time.
        //
        // Reduced-order position (i.e. position among only the non-moving buttons) where each
        // block begins; length is block count + 1, with the final entry being the total non-moving
        // item count (the position just past the last block).
        private List<int> _dragBlockStartPos = new List<int>();
        // Current gap index: which block boundary the dragged block currently occupies.
        // 0 = before all blocks, blockCount = after all.
        private int _dragBlockGap;

        public bool IsDraggingButton => _isDragging;

        public void StartButtonDrag(TaskButton button, MouseEventArgs e, bool soloDrag = false)
        {
            if (_isDragging || button == null || TasksList.Items.Count < 2)
                return;

            _dragPanel = FindItemsPanel<WrapPanel>(TasksList);
            if (_dragPanel == null)
                return;

            // Snapshot the current containers and their stable (untransformed) layout slots.
            _dragContainers = new List<ContentPresenter>();
            _dragSlots = new List<Point>();
            _dragSizes = new List<Size>();
            _dragFromIndex = -1;

            for (int i = 0; i < TasksList.Items.Count; i++)
            {
                if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp)
                    return;

                cp.RenderTransform = null;
                _dragContainers.Add(cp);
                _dragSlots.Add(cp.TranslatePoint(new Point(0, 0), _dragPanel));
                _dragSizes.Add(new Size(cp.ActualWidth, cp.ActualHeight));

                if (ReferenceEquals(cp.DataContext, button.DataContext))
                    _dragFromIndex = i;
            }

            if (_dragFromIndex < 0)
                return;

            _isDragging = true;
            _dragContainer = _dragContainers[_dragFromIndex];
            _dragToIndex = _dragFromIndex;
            _dragStartPanelPoint = e.GetPosition(_dragPanel);
            _dragSiblingTargets.Clear();

            // The button already holds mouse capture from its press, so cursor tracking and the
            // capture-loss that ends the drag both flow through that existing capture.
            Panel.SetZIndex(_dragContainer, 100);

            // Collect group members that must travel with the primary dragged button.
            _dragGroupMemberIndices = new List<int>();
            _dragGroupSorted = new List<int>();
            _dragGroupPrimaryOffset = 0;

            bool horizontal = Host?.Orientation != Orientation.Vertical;

            var draggedWindow = _dragContainer.DataContext as ApplicationWindow;
            var draggedGroup = draggedWindow != null ? _groupManager.GetGroupForWindow(draggedWindow) : null;

            // A collapsed group normally shows only its representative button; there is no
            // meaningful "pull one button out" gesture while the rest are hidden, so ignore ctrl
            // and drag the whole group as a block (the collapsed-group handling in CommitDrag keeps
            // its members together). But an active window forces the group to render fully expanded
            // (see Tasks_Filter) - in that case every member is its own visible button, same as an
            // uncollapsed group, so solo drag is meaningful and must not be suppressed.
            if (soloDrag && draggedGroup != null && draggedGroup.IsCollapsed && draggedGroup.Windows.Count > 1
                && !_groupManager.GroupHasActiveWindow(draggedGroup))
                soloDrag = false;

            _dragOriginalGroup = draggedGroup;
            _dragIsSolo = soloDrag;
            _dragSoloOriginalGroup = soloDrag ? draggedGroup : null;

            if (!soloDrag && draggedGroup != null && draggedGroup.Windows.Count > 1)
            {
                for (int i = 0; i < _dragContainers.Count; i++)
                {
                    if (i == _dragFromIndex) continue;
                    if (_dragContainers[i].DataContext is ApplicationWindow w && draggedGroup.Windows.Contains(w))
                    {
                        _dragGroupMemberIndices.Add(i);
                        Panel.SetZIndex(_dragContainers[i], 99);
                    }
                }

                _dragGroupSorted = new List<int>(_dragGroupMemberIndices) { _dragFromIndex };
                _dragGroupSorted.Sort((a, b) =>
                    MainCoord(_dragSlots[a], horizontal).CompareTo(MainCoord(_dragSlots[b], horizontal)));
                _dragGroupPrimaryOffset = _dragGroupSorted.IndexOf(_dragFromIndex);
            }

            ComputeDragBlocks();
        }

        // Partitions the non-moving buttons into blocks: a run of members belonging to the same
        // foreign task group travels as one indivisible block, everything else is its own singleton
        // block. Swaps are then evaluated against a whole block at once, so a multi-button group is
        // treated like one wide button rather than being crossed — and split apart — one member at
        // a time.
        private void ComputeDragBlocks()
        {
            var movingIndices = _dragGroupSorted.Count > 0
                ? new HashSet<int>(_dragGroupSorted)
                : new HashSet<int> { _dragFromIndex };

            var blocks = new List<List<int>>();
            TaskGroup currentGroup = null;
            var soloGroupBlockIndices = new List<int>();
            var soloGroupItemIndices = new List<int>();

            for (int i = 0; i < _dragContainers.Count; i++)
            {
                if (movingIndices.Contains(i)) continue;

                var window = _dragContainers[i].DataContext as ApplicationWindow;
                var group = window != null ? _groupManager.GetGroupForWindow(window) : null;

                // The solo-dragged button's own former group is left splittable, so the drag can
                // land between its former groupmates (reorder within the group) or past either end
                // (leave the group), rather than being blocked from entering its own old group.
                bool splittable = _dragSoloOriginalGroup != null && ReferenceEquals(group, _dragSoloOriginalGroup);

                if (!splittable && group != null && ReferenceEquals(group, currentGroup))
                {
                    blocks[blocks.Count - 1].Add(i);
                }
                else
                {
                    blocks.Add(new List<int> { i });
                    currentGroup = splittable ? null : group;
                    if (splittable)
                    {
                        soloGroupBlockIndices.Add(blocks.Count - 1);
                        soloGroupItemIndices.Add(i);
                    }
                }
            }

            _dragSoloGroupBlockFirst = soloGroupBlockIndices.Count > 0 ? soloGroupBlockIndices[0] : -1;
            _dragSoloGroupBlockLast = soloGroupBlockIndices.Count > 0 ? soloGroupBlockIndices[soloGroupBlockIndices.Count - 1] : -1;
            _dragSoloGroupFirstItemIdx = soloGroupItemIndices.Count > 0 ? soloGroupItemIndices[0] : -1;
            _dragSoloGroupLastItemIdx = soloGroupItemIndices.Count > 0 ? soloGroupItemIndices[soloGroupItemIndices.Count - 1] : -1;

            _dragBlockStartPos = new List<int>(blocks.Count + 1) { 0 };
            int pos = 0;

            foreach (var block in blocks)
            {
                pos += block.Count;
                _dragBlockStartPos.Add(pos);
            }

            int primaryOffset = _dragGroupSorted.Count > 0 ? _dragGroupPrimaryOffset : 0;
            int initialPos = _dragFromIndex - primaryOffset;

            int gap = _dragBlockStartPos.IndexOf(initialPos);
            if (gap < 0)
            {
                // The dragged item's own starting position doesn't line up with a block boundary —
                // only possible if a group was already split before this drag began. Snap forward
                // to the next boundary as a best-effort recovery.
                gap = 0;
                while (gap < _dragBlockStartPos.Count - 1 && _dragBlockStartPos[gap] < initialPos) gap++;
            }
            _dragBlockGap = gap;

            _dragSoloInGroup = _dragSoloOriginalGroup != null
                && gap >= _dragSoloGroupBlockFirst && gap <= _dragSoloGroupBlockLast + 1;
        }


        public void UpdateButtonDrag(MouseEventArgs e)
        {
            if (!_isDragging)
                return;

            bool horizontal = Host?.Orientation != Orientation.Vertical;
            Vector delta = e.GetPosition(_dragPanel) - _dragStartPanelPoint;

            // Move the dragged button with the cursor along the main axis only.
            var draggedTransform = GetTranslate(_dragContainer);
            draggedTransform.BeginAnimation(TranslateTransform.XProperty, null);
            draggedTransform.BeginAnimation(TranslateTransform.YProperty, null);
            if (horizontal)
            {
                draggedTransform.X = delta.X;
                draggedTransform.Y = 0;
            }
            else
            {
                draggedTransform.X = 0;
                draggedTransform.Y = delta.Y;
            }

            // All group members travel with the same cursor delta.
            foreach (int gi in _dragGroupMemberIndices)
            {
                var gt = GetTranslate(_dragContainers[gi]);
                gt.BeginAnimation(TranslateTransform.XProperty, null);
                gt.BeginAnimation(TranslateTransform.YProperty, null);
                if (horizontal)
                {
                    gt.X = delta.X;
                    gt.Y = 0;
                }
                else
                {
                    gt.X = 0;
                    gt.Y = delta.Y;
                }
            }

            // The dragged block and the neighbouring block are always adjacent in display order, so
            // together they span one combined range. A swap fires when the dragged block's midpoint
            // crosses the midpoint of that combined span. The combined span occupies the same total
            // extent whether or not the pair has swapped, so its midpoint is invariant under the
            // swap — the forward and reverse thresholds are the exact same line. Crossing it swaps
            // once; only re-crossing that same line swaps back. There is no threshold band at all
            // and no feedback (thresholds derive from static slot geometry, not animated
            // positions), so no hysteresis and no back-and-forth fighting is possible. This holds
            // for any dragged-block width against any target-block width.
            double deltaMain = horizontal ? delta.X : delta.Y;

            int moving = _dragGroupSorted.Count > 0 ? _dragGroupSorted.Count : 1;
            int movingFirst = _dragGroupSorted.Count > 0 ? _dragGroupSorted[0] : _dragFromIndex;
            int movingLast = _dragGroupSorted.Count > 0 ? _dragGroupSorted[_dragGroupSorted.Count - 1] : _dragFromIndex;

            double draggedCenter = (MainCoord(_dragSlots[movingFirst], horizontal)
                                    + MainCoord(_dragSlots[movingLast], horizontal)
                                    + MainSize(_dragSizes[movingLast], horizontal)) / 2.0 + deltaMain;

            int gap = _dragBlockGap;
            int blockCount = _dragBlockStartPos.Count - 1;

            while (true)
            {
                // The hole (the dragged block's current home) spans visual slots
                // [holeFirst, holeFirst + moving - 1].
                int holeFirst = _dragBlockStartPos[gap];

                if (gap < blockCount)
                {
                    // Combined span: hole plus the next block (displaced past the hole, so its
                    // buttons occupy visual slots offset by the moving count).
                    int lastSlot = _dragBlockStartPos[gap + 1] - 1 + moving;
                    double leading = MainCoord(_dragSlots[holeFirst], horizontal);
                    double trailing = MainCoord(_dragSlots[lastSlot], horizontal)
                                      + MainSize(_dragSizes[lastSlot], horizontal);
                    if (draggedCenter > (leading + trailing) / 2.0)
                    {
                        gap++;
                        continue;
                    }
                }

                if (gap > 0)
                {
                    // Combined span: the previous block (at its unshifted slots) plus the hole.
                    int firstSlot = _dragBlockStartPos[gap - 1];
                    int holeLast = holeFirst + moving - 1;
                    double leading = MainCoord(_dragSlots[firstSlot], horizontal);
                    double trailing = MainCoord(_dragSlots[holeLast], horizontal)
                                      + MainSize(_dragSizes[holeLast], horizontal);
                    if (draggedCenter < (leading + trailing) / 2.0)
                    {
                        gap--;
                        continue;
                    }
                }

                break;
            }

            if (gap != _dragBlockGap)
            {
                _dragBlockGap = gap;
                int primaryOffset = _dragGroupSorted.Count > 0 ? _dragGroupPrimaryOffset : 0;
                _dragToIndex = _dragBlockStartPos[gap] + primaryOffset;
                LayoutDragSiblings(horizontal);
            }

            // Leaving the original group: while nested between two former groupmates it's
            // unambiguously still in; once it's swapped a full block past the group it's
            // unambiguously out. Right at the group's edge (touching it, nothing between) is the
            // ambiguous case - only counts as "left" once it's dragged a quarter of its own width
            // past that edge. Checked every move (not just on gap change) since distance keeps
            // growing as the cursor keeps moving even once the gap itself stops changing. Re-entering
            // is deliberately harder - see the rejoin-hover handling below.
            if (_dragIsSolo && _dragSoloOriginalGroup != null && _dragSoloInGroup
                && !IsSoloStillWithinGroup(gap, delta, horizontal))
            {
                _dragSoloInGroup = false;
                _groupManager.SetSoloDragVisual(_dragContainer, _dragSoloOriginalGroup, false);
                CancelGroupHover();
            }

            if (_dragIsSolo)
            {
                // Solo drag: never invite a merge into a different group. The only hover-confirm
                // gesture allowed is rejoining this button's own original group once it's left it.
                if (_dragSoloOriginalGroup != null && !_dragSoloInGroup)
                {
                    var draggedRect = GetDraggedRect(delta, horizontal);
                    int overlapIdx = -1;

                    for (int i = 0; i < _dragContainers.Count; i++)
                    {
                        if (i == _dragFromIndex) continue;
                        if (_dragContainers[i].DataContext is not ApplicationWindow w || !_dragSoloOriginalGroup.Windows.Contains(w))
                            continue;
                        if (OverlapFraction(draggedRect, GetSlotRect(i), horizontal) >= 0.25f)
                        {
                            overlapIdx = i;
                            break;
                        }
                    }

                    if (overlapIdx != _groupManager.HoverTargetIndex)
                    {
                        CancelGroupHover();
                        if (overlapIdx >= 0)
                            StartSoloRejoinHover(overlapIdx);
                    }
                }
            }
            // Group hover detection (merge into a new/different group): only when dragging a
            // single button that isn't already in a group. Checking _dragOriginalGroup (not just
            // the rendered member count) ensures a grouped button whose groupmates aren't currently
            // shown still can't drag its whole group into another group - groups never merge.
            else if (_dragOriginalGroup == null && _dragGroupMemberIndices.Count == 0)
            {
                var draggedRect = GetDraggedRect(delta, horizontal);
                int overlapIdx = -1;

                for (int i = 0; i < _dragContainers.Count; i++)
                {
                    if (i == _dragFromIndex) continue;
                    if (OverlapFraction(draggedRect, GetSlotRect(i), horizontal) >= 0.25f)
                    {
                        overlapIdx = i;
                        break;
                    }
                }

                if (overlapIdx != _groupManager.HoverTargetIndex)
                {
                    // Cancel previous state (even if confirmed — dragging away always cancels).
                    CancelGroupHover();
                    if (overlapIdx >= 0)
                        StartGroupHover(overlapIdx);
                }
            }
        }

        public void EndButtonDrag()
        {
            if (!_isDragging)
                return;

            // Stop tracking the cursor immediately so a stray mouse move during the settle
            // animation can't restart UpdateButtonDrag and cancel the snap (leaving it stuck).
            _isDragging = false;

            // Commit or cancel provisional group before starting snap animation. The group
            // membership (data only) is committed now, but the collection Refresh is deferred to the
            // end of CommitDrag: refreshing here regenerates every container mid-snap, detaching the
            // dragged button so it appears to snap back to its origin.
            _dragCommittedMerge = false;
            if (_groupManager.HoverConfirmed)
            {
                _groupManager.CommitProvisionalGroup();
                _dragCommittedMerge = true;
            }
            else
                CancelGroupHover();

            bool horizontal = Host?.Orientation != Orientation.Vertical;
            Vector finalOffset = _dragSlots[_dragToIndex] - _dragSlots[_dragFromIndex];
            var transform = GetTranslate(_dragContainer);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);

            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            var animX = new DoubleAnimation(transform.X, finalOffset.X, TimeSpan.FromMilliseconds(DragSnapMs)) { EasingFunction = ease };
            var animY = new DoubleAnimation(transform.Y, finalOffset.Y, TimeSpan.FromMilliseconds(DragSnapMs)) { EasingFunction = ease };
            animX.Completed += (_, _) => CommitDrag();
            transform.BeginAnimation(TranslateTransform.XProperty, animX);
            transform.BeginAnimation(TranslateTransform.YProperty, animY);

            // Snap each group member to its final resting slot.
            if (_dragGroupSorted.Count > 0)
            {
                int count = _dragContainers.Count;
                int groupSize = _dragGroupSorted.Count;
                int groupStart = Math.Max(0, Math.Min(_dragToIndex - _dragGroupPrimaryOffset, count - groupSize));

                for (int k = 0; k < groupSize; k++)
                {
                    int idx = _dragGroupSorted[k];
                    if (idx == _dragFromIndex) continue;
                    int targetSlot = groupStart + k;
                    Vector memberFinal = _dragSlots[targetSlot] - _dragSlots[idx];
                    var gt = GetTranslate(_dragContainers[idx]);
                    gt.BeginAnimation(TranslateTransform.XProperty, null);
                    gt.BeginAnimation(TranslateTransform.YProperty, null);
                    gt.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(gt.X, memberFinal.X, TimeSpan.FromMilliseconds(DragSnapMs)) { EasingFunction = ease });
                    gt.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(gt.Y, memberFinal.Y, TimeSpan.FromMilliseconds(DragSnapMs)) { EasingFunction = ease });
                }
            }
        }

        private void LayoutDragSiblings(bool horizontal)
        {
            if (_dragGroupSorted.Count > 0)
            {
                LayoutDragSiblingsGroup(horizontal);
                return;
            }

            // Build the display order with the dragged item reinserted at the target index.
            var order = new List<int>(_dragContainers.Count);
            for (int i = 0; i < _dragContainers.Count; i++)
            {
                if (i != _dragFromIndex) order.Add(i);
            }
            order.Insert(_dragToIndex, _dragFromIndex);

            for (int pos = 0; pos < order.Count; pos++)
            {
                int idx = order[pos];
                if (idx == _dragFromIndex) continue; // dragged button follows the cursor

                Vector offset = _dragSlots[pos] - _dragSlots[idx];
                AnimateSibling(_dragContainers[idx], offset);
            }
        }

        private void LayoutDragSiblingsGroup(bool horizontal)
        {
            int count = _dragContainers.Count;
            int groupSize = _dragGroupSorted.Count;
            int groupStart = Math.Max(0, Math.Min(_dragToIndex - _dragGroupPrimaryOffset, count - groupSize));

            // Non-group buttons in their original slot order.
            var nonGroup = new List<int>(count - groupSize);
            for (int i = 0; i < count; i++)
                if (!_dragGroupSorted.Contains(i)) nonGroup.Add(i);

            int nonGroupPos = 0;
            for (int visualPos = 0; visualPos < count; visualPos++)
            {
                if (visualPos >= groupStart && visualPos < groupStart + groupSize)
                {
                    // Slot occupied by a group member — they follow the cursor directly, no animation.
                    continue;
                }

                int idx = nonGroup[nonGroupPos++];
                Vector offset = _dragSlots[visualPos] - _dragSlots[idx];
                AnimateSibling(_dragContainers[idx], offset);
            }
        }

        private void AnimateSibling(ContentPresenter cp, Vector offset)
        {
            Point target = new Point(offset.X, offset.Y);
            if (_dragSiblingTargets.TryGetValue(cp, out Point current) && current == target)
                return;

            _dragSiblingTargets[cp] = target;

            var transform = GetTranslate(cp);
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(offset.X, TimeSpan.FromMilliseconds(DragAnimationMs)) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(offset.Y, TimeSpan.FromMilliseconds(DragAnimationMs)) { EasingFunction = ease });
        }

        private void CommitDrag()
        {
            if (_dragContainers == null)
                return;

            int from = _dragFromIndex;
            int to = _dragToIndex;
            bool horizontal = Host?.Orientation != Orientation.Vertical;

            var source = taskbarItems?.SourceCollection as ObservableCollection<ApplicationWindow>;
            if (source != null)
            {
                if (_dragGroupSorted.Count > 0)
                {
                    // Reorder collection to match the group block's final visual order.
                    int count = _dragContainers.Count;
                    int groupSize = _dragGroupSorted.Count;
                    int groupStart = Math.Max(0, Math.Min(to - _dragGroupPrimaryOffset, count - groupSize));

                    var nonGroup = new List<int>(count - groupSize);
                    for (int i = 0; i < count; i++)
                        if (!_dragGroupSorted.Contains(i)) nonGroup.Add(i);

                    // Build the desired visual order list.
                    var order = new List<int>(count);
                    int ngPos = 0;
                    for (int pos = 0; pos < count; pos++)
                    {
                        if (pos >= groupStart && pos < groupStart + groupSize)
                            order.Add(_dragGroupSorted[pos - groupStart]);
                        else
                            order.Add(nonGroup[ngPos++]);
                    }

                    // Apply the desired order to the source collection.
                    var desired = order.Select(i => _dragContainers[i].DataContext as ApplicationWindow).ToList();
                    for (int i = 0; i < desired.Count; i++)
                    {
                        int cur = source.IndexOf(desired[i]);
                        if (cur != i) source.Move(cur, i);
                    }
                }
                else if (to != from && _dragContainer.DataContext is ApplicationWindow dragged)
                {
                    // Determine the window the dragged button now precedes in the new visual order.
                    var order = new List<int>(_dragContainers.Count);
                    for (int i = 0; i < _dragContainers.Count; i++)
                    {
                        if (i != from) order.Add(i);
                    }
                    order.Insert(to, from);

                    ApplicationWindow neighbor = null;
                    if (to + 1 < order.Count)
                    {
                        neighbor = _dragContainers[order[to + 1]].DataContext as ApplicationWindow;
                    }

                    int srcFrom = source.IndexOf(dragged);
                    if (srcFrom >= 0)
                    {
                        int srcTo;
                        if (neighbor != null)
                        {
                            srcTo = source.IndexOf(neighbor);
                            if (srcTo > srcFrom) srcTo--;
                        }
                        else
                        {
                            srcTo = source.Count - 1;
                        }

                        if (srcTo >= 0 && srcTo != srcFrom)
                            source.Move(srcFrom, srcTo);
                    }
                }
            }

            // A collapsed group shows only its representative button, so the reorder above (which
            // positions windows by their compact *visible* order) both moves just the dragged
            // group's representative — leaving its hidden members behind — and can shove an
            // unrelated collapsed group's hidden members apart when a visible window lands at a
            // source index that falls inside that group's run. Either way uncollapsing would reveal
            // a group split across the taskbar. Re-contiguate every collapsed group so each one's
            // hidden members sit right after its representative again, keeping groups intact.
            if (source != null)
            {
                foreach (var group in _groupManager.Groups.ToList())
                {
                    if (!group.IsCollapsed || group.Windows.Count <= 1
                        || _groupManager.GroupHasActiveWindow(group))
                        continue;

                    // Members in current source order (the representative is leftmost, so first).
                    var members = group.Windows
                        .Select(w => (w, idx: source.IndexOf(w)))
                        .Where(x => x.idx >= 0)
                        .OrderBy(x => x.idx)
                        .Select(x => x.w)
                        .ToList();

                    for (int i = 1; i < members.Count; i++)
                    {
                        int cur = source.IndexOf(members[i]);
                        int prev = source.IndexOf(members[i - 1]);
                        if (cur < 0 || prev < 0) continue;
                        int target = prev + 1;
                        if (cur < target) target--;
                        if (cur != target) source.Move(cur, target);
                    }
                }
            }

            // Ctrl+drag of a single button out of/within its group: commit whatever in/out-of-group
            // state was last shown live during the drag. Skipped if a hover-merge already moved it
            // into a different group (that path resolves membership itself).
            if (_dragIsSolo && _dragSoloOriginalGroup != null
                && _dragContainer.DataContext is ApplicationWindow soloWindow
                && ReferenceEquals(_groupManager.GetGroupForWindow(soloWindow), _dragSoloOriginalGroup))
            {
                _groupManager.ResolveSoloGroupDrag(soloWindow, _dragSoloOriginalGroup, _dragSoloInGroup);
                taskbarItems?.Refresh();
            }

            // Clear all transforms; the new layout already reflects the committed order so this
            // happens in the same render pass as the collection move (no flicker).
            foreach (var cp in _dragContainers)
            {
                if (cp.RenderTransform is TranslateTransform t)
                {
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                }
                cp.RenderTransform = null;
                Panel.SetZIndex(cp, 0);
            }

            _isDragging = false;
            _dragContainer = null;
            _dragContainers = null;
            _dragSlots = null;
            _dragSizes = null;
            _dragPanel = null;
            _dragSiblingTargets.Clear();
            _dragGroupMemberIndices = new List<int>();
            _dragGroupSorted = new List<int>();
            _dragGroupPrimaryOffset = 0;
            _dragBlockStartPos = new List<int>();
            _dragBlockGap = 0;
            _dragIsSolo = false;
            _dragOriginalGroup = null;
            _dragSoloOriginalGroup = null;
            _dragSoloGroupBlockFirst = -1;
            _dragSoloGroupBlockLast = -1;
            _dragSoloGroupFirstItemIdx = -1;
            _dragSoloGroupLastItemIdx = -1;
            _dragSoloInGroup = false;

            bool committedMerge = _dragCommittedMerge;
            _dragCommittedMerge = false;

            // Reapply group visuals after layout settles (collection moves may recycle containers).
            // If a hover-merge committed, run the deferred collection Refresh here too (re-runs the
            // filter, e.g. hiding members when merging into a collapsed group). Doing it now — after
            // the source reorder and transform cleanup — keeps the snap animation's containers valid,
            // whereas refreshing at drag-end regenerated them and caused the button to snap back.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                if (committedMerge)
                    taskbarItems?.Refresh();
                _groupManager.ApplyGroupVisuals();
            }));
        }

        private static TranslateTransform GetTranslate(ContentPresenter cp)
        {
            if (cp.RenderTransform is TranslateTransform t)
                return t;

            var transform = new TranslateTransform();
            cp.RenderTransform = transform;
            return transform;
        }

        private static double MainCoord(Point p, bool horizontal) => horizontal ? p.X : p.Y;

        private static double MainSize(Size s, bool horizontal) => horizontal ? s.Width : s.Height;

        #endregion
    }
}
