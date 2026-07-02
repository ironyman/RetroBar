using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register(nameof(ButtonWidth), typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

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

                Settings.Instance.PropertyChanged += Settings_PropertyChanged;
                Host.hotkeyManager.TaskbarHotkeyPressed += TaskList_TaskbarHotkeyPressed;
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

            if (Host != null)
            {
                Host.hotkeyManager.TaskbarHotkeyPressed -= TaskList_TaskbarHotkeyPressed;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            WorkspaceManager.Instance.WorkspaceSwitched -= WorkspaceManager_WorkspaceSwitched;

            isLoaded = false;
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();

            var action = e.Action;

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

                foreach (var group in _taskGroups)
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
                foreach (ApplicationWindow window in e.OldItems)
                {
                    var group = GetGroupForWindow(window);
                    if (group == null) continue;
                    group.Windows.Remove(window);
                    if (group.Windows.Count <= 1)
                        _taskGroups.Remove(group);
                    changed = true;
                }
            }
            else if (action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                // Full reset — dissolve all groups whose members are no longer in the source.
                if (taskbarItems?.SourceCollection is System.Collections.IEnumerable src)
                {
                    var remaining = new HashSet<ApplicationWindow>(src.OfType<ApplicationWindow>());
                    foreach (var g in _taskGroups.ToList())
                    {
                        g.Windows.RemoveAll(w => !remaining.Contains(w));
                        if (g.Windows.Count <= 1) _taskGroups.Remove(g);
                    }
                }
                changed = true;
            }

            if (changed)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)ApplyGroupVisuals);
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ShellLogger.Debug($"TaskList: SizeChanged oldSize={e.PreviousSize.Width:F3}x{e.PreviousSize.Height:F3} newSize={e.NewSize.Width:F3}x{e.NewSize.Height:F3} TasksList.ActualWidth={TasksList.ActualWidth:F3}");
            SetTaskButtonWidth();
        }

        private void SetTaskButtonWidth()
        {
            if (Host is null)
                return; // The state is trashed, but presumably it's just a transition

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                ButtonWidth = ActualWidth;
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
            ButtonWidth = newButtonWidth;

            // Post-layout check: confirm actual layout after WPF processes the width change
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                var wrapPanel = FindItemsPanel<WrapPanel>(TasksList);
                double wrapHeight = wrapPanel?.ActualHeight ?? -1;
                double wrapWidth = wrapPanel?.ActualWidth ?? -1;
                ShellLogger.Debug($"TaskList: Post-layout TasksList.ActualWidth={TasksList.ActualWidth:F3} ButtonWidth={ButtonWidth:F3} itemCount={TasksList.Items.Count} wrapPanel={wrapWidth:F3}x{wrapHeight:F3} taskbarHeight={ActualHeight:F3} wrapping={wrapHeight > ActualHeight + 1}");
            }));
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

        private readonly List<TaskGroup> _taskGroups = new List<TaskGroup>();

        // During-drag provisional group (shown as stripe preview; committed on mouse release).
        private DispatcherTimer _groupHoverTimer;
        private int _groupHoverTargetIndex = -1;
        private bool _groupHoverConfirmed;
        private TaskGroup _provisionalGroup;
        private const double GroupHoverMs = 300;

        private TaskGroup GetGroupForWindow(ApplicationWindow window)
            => _taskGroups.FirstOrDefault(g => g.Windows.Contains(window));

        private static TaskButton GetTaskButton(ContentPresenter cp)
        {
            if (cp == null || VisualTreeHelper.GetChildrenCount(cp) == 0) return null;
            return VisualTreeHelper.GetChild(cp, 0) as TaskButton;
        }

        // Schedules a visual refresh on the next layout pass so containers are ready.
        private void UpdateGroupVisuals()
            => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)ApplyGroupVisuals);

        private void ApplyGroupVisuals()
        {
            for (int i = 0; i < TasksList.Items.Count; i++)
            {
                if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
                var btn = GetTaskButton(cp);
                if (btn == null) continue;
                var window = cp.DataContext as ApplicationWindow;
                if (window == null) continue;

                var group = GetGroupForWindow(window);
                if (group != null)
                {
                    btn.SetGroupColor(group.GroupColor);
                    continue;
                }

                if (_provisionalGroup?.Windows.Contains(window) == true)
                {
                    btn.SetGroupColor(_provisionalGroup.GroupColor);
                    continue;
                }

                btn.SetGroupColor(null);
            }
        }

        // Called by TaskButton.Loaded so new buttons pick up their group color.
        public void RefreshGroupVisual(TaskButton btn)
        {
            if (btn?.DataContext is not ApplicationWindow window) return;
            var group = GetGroupForWindow(window);
            if (group != null)
                btn.SetGroupColor(group.GroupColor);
            else if (_provisionalGroup?.Windows.Contains(window) == true)
                btn.SetGroupColor(_provisionalGroup.GroupColor);
            else
                btn.SetGroupColor(null);
        }

        // Called from TaskButton right-click → Remove from group.
        public void UngroupWindow(ApplicationWindow window)
        {
            if (window == null) return;
            var group = GetGroupForWindow(window);
            if (group == null) return;

            group.Windows.Remove(window);

            if (taskbarItems?.SourceCollection is ObservableCollection<ApplicationWindow> source)
            {
                int windowPos = source.IndexOf(window);
                if (windowPos >= 0 && group.Windows.Count > 0)
                {
                    // Find the span of remaining group members in collection order.
                    var groupPositions = group.Windows
                        .Select(w => source.IndexOf(w))
                        .Where(idx => idx >= 0)
                        .OrderBy(idx => idx)
                        .ToList();

                    if (groupPositions.Count > 0)
                    {
                        int firstGroupIdx = groupPositions[0];
                        int lastGroupIdx = groupPositions[groupPositions.Count - 1];

                        // Place before or after the group based on which end is closer.
                        bool placeBeforeGroup = Math.Abs(windowPos - firstGroupIdx) <= Math.Abs(windowPos - lastGroupIdx);

                        int targetPos;
                        if (placeBeforeGroup)
                        {
                            // Insert before the first group member.
                            // After removing windowPos, firstGroupIdx shifts down if windowPos < it.
                            targetPos = windowPos < firstGroupIdx ? firstGroupIdx - 1 : firstGroupIdx;
                        }
                        else
                        {
                            // Insert after the last group member.
                            // After removing windowPos, lastGroupIdx shifts down if windowPos < it.
                            targetPos = windowPos < lastGroupIdx ? lastGroupIdx : lastGroupIdx + 1;
                        }

                        targetPos = Math.Max(0, Math.Min(targetPos, source.Count - 1));
                        if (targetPos != windowPos)
                            source.Move(windowPos, targetPos);
                    }
                }
            }

            if (group.Windows.Count <= 1)
                _taskGroups.Remove(group);

            UpdateGroupVisuals();
        }

        // Called from TaskButton right-click → New color for group.
        public void ChangeGroupColor(ApplicationWindow window)
        {
            var group = GetGroupForWindow(window);
            if (group == null) return;
            group.GroupColor = TaskGroup.RandomColor();
            UpdateGroupVisuals();
        }

        // Called from TaskButton right-click → Remove group.
        // Dissolves group membership for all windows without repositioning them.
        public void RemoveGroup(ApplicationWindow window)
        {
            if (window == null) return;
            var group = GetGroupForWindow(window);
            if (group == null) return;
            _taskGroups.Remove(group);
            UpdateGroupVisuals();
        }

        // Returns the windows belonging to the same group as the given window.
        public List<ApplicationWindow> GetGroupWindows(ApplicationWindow window)
        {
            var group = GetGroupForWindow(window);
            if (group == null) return new List<ApplicationWindow> { window };
            return new List<ApplicationWindow>(group.Windows);
        }

        // Collapses the group containing the given window.
        public void CollapseGroup(ApplicationWindow window)
        {
            if (window == null) return;
            var group = GetGroupForWindow(window);
            if (group == null) return;
            group.IsCollapsed = true;
            UpdateGroupVisuals();
        }

        // Collapses all task groups.
        public void CollapseAllGroups()
        {
            foreach (var group in _taskGroups)
                group.IsCollapsed = true;
            UpdateGroupVisuals();
        }

        // Uncollapses all task groups.
        public void UncollapseAllGroups()
        {
            foreach (var group in _taskGroups)
                group.IsCollapsed = false;
            UpdateGroupVisuals();
        }

        private void StartGroupHover(int targetIndex)
        {
            _groupHoverTargetIndex = targetIndex;
            _groupHoverTimer?.Stop();
            _groupHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GroupHoverMs) };
            _groupHoverTimer.Tick += (_, _) => OnGroupHoverConfirmed();
            _groupHoverTimer.Start();
        }

        private void CancelGroupHover()
        {
            _groupHoverTimer?.Stop();
            _groupHoverTimer = null;

            if (_groupHoverConfirmed)
            {
                _groupHoverConfirmed = false;
                _provisionalGroup = null;
                ApplyGroupVisuals();
            }

            _groupHoverTargetIndex = -1;
        }

        private void OnGroupHoverConfirmed()
        {
            _groupHoverTimer?.Stop();
            _groupHoverTimer = null;

            if (!_isDragging || _groupHoverTargetIndex < 0 || _dragContainer == null) return;

            var draggedWindow = _dragContainer.DataContext as ApplicationWindow;
            if (_groupHoverTargetIndex >= _dragContainers.Count) return;
            var targetWindow = _dragContainers[_groupHoverTargetIndex].DataContext as ApplicationWindow;
            if (draggedWindow == null || targetWindow == null) return;

            var draggedGroup = GetGroupForWindow(draggedWindow);
            var targetGroup = GetGroupForWindow(targetWindow);

            // Pick the color from whichever side already has a group; otherwise random.
            Color color = (targetGroup ?? draggedGroup)?.GroupColor ?? TaskGroup.RandomColor();

            _provisionalGroup = new TaskGroup(color);

            // Merge both sides into the provisional display set.
            if (draggedGroup != null)
                foreach (var w in draggedGroup.Windows) _provisionalGroup.Windows.Add(w);
            else
                _provisionalGroup.Windows.Add(draggedWindow);

            if (targetGroup != null)
            {
                foreach (var w in targetGroup.Windows)
                    if (!_provisionalGroup.Windows.Contains(w)) _provisionalGroup.Windows.Add(w);
            }
            else
            {
                if (!_provisionalGroup.Windows.Contains(targetWindow))
                    _provisionalGroup.Windows.Add(targetWindow);
            }

            _groupHoverConfirmed = true;
            ApplyGroupVisuals();
        }

        private void CommitGroupFromProvisional()
        {
            if (!_groupHoverConfirmed || _provisionalGroup == null) return;

            // Collect old groups that contain any provisional member.
            var oldGroups = new HashSet<TaskGroup>();
            foreach (var w in _provisionalGroup.Windows)
            {
                var g = GetGroupForWindow(w);
                if (g != null) oldGroups.Add(g);
            }

            // Strip those windows from their old groups (may dissolve them).
            foreach (var g in oldGroups)
            {
                g.Windows.RemoveAll(w => _provisionalGroup.Windows.Contains(w));
                if (g.Windows.Count <= 1)
                    _taskGroups.Remove(g);
            }

            // The provisional object becomes the committed group.
            _taskGroups.Add(_provisionalGroup);
            _provisionalGroup = null;
            _groupHoverConfirmed = false;
            _groupHoverTargetIndex = -1;
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

        public void StartButtonDrag(TaskButton button, MouseEventArgs e)
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
            var draggedGroup = draggedWindow != null ? GetGroupForWindow(draggedWindow) : null;
            if (draggedGroup != null && draggedGroup.Windows.Count > 1)
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

            for (int i = 0; i < _dragContainers.Count; i++)
            {
                if (movingIndices.Contains(i)) continue;

                var window = _dragContainers[i].DataContext as ApplicationWindow;
                var group = window != null ? GetGroupForWindow(window) : null;

                if (group != null && ReferenceEquals(group, currentGroup))
                {
                    blocks[blocks.Count - 1].Add(i);
                }
                else
                {
                    blocks.Add(new List<int> { i });
                    currentGroup = group;
                }
            }

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

            // Group hover detection: only when dragging a single ungrouped button.
            if (_dragGroupMemberIndices.Count == 0)
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

                if (overlapIdx != _groupHoverTargetIndex)
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

            // Commit or cancel provisional group before starting snap animation.
            if (_groupHoverConfirmed)
                CommitGroupFromProvisional();
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

            if (taskbarItems?.SourceCollection is ObservableCollection<ApplicationWindow> source)
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

            // Reapply group visuals after layout settles (collection moves may recycle containers).
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)ApplyGroupVisuals);
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
