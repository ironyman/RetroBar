using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    // Owns task-group membership, the provisional (drag-preview) group, hover-to-merge timing,
    // and group-color visuals for a TaskList. TaskList keeps drag mechanics (which button goes
    // where) to itself and delegates anything group-related here.
    public class TaskGroupManager
    {
        private const double GroupHoverMs = 300;

        private readonly ItemsControl _tasksList;
        private readonly Dispatcher _dispatcher;

        private const double DesktopSplitCheckMs = 250;

        private readonly List<TaskGroup> _taskGroups = new List<TaskGroup>();
        private DispatcherTimer _groupHoverTimer;
        private int _groupHoverTargetIndex = -1;
        private bool _groupHoverConfirmed;
        private TaskGroup _provisionalGroup;
        private DispatcherTimer _desktopSplitTimer;
        private ObservableCollection<ApplicationWindow> _source;

        public TaskGroupManager(ItemsControl tasksList, Dispatcher dispatcher)
        {
            _tasksList = tasksList;
            _dispatcher = dispatcher;
        }

        // Groups track membership by HWND (see TaskGroup.Handles); this is the collection live
        // ApplicationWindow instances are resolved from.
        public void SetSource(ObservableCollection<ApplicationWindow> source)
        {
            _source = source;
        }

        public IReadOnlyList<TaskGroup> Groups => _taskGroups;
        public bool HoverConfirmed => _groupHoverConfirmed;
        public int HoverTargetIndex => _groupHoverTargetIndex;

        public TaskGroup GetGroupForWindow(ApplicationWindow window)
            => window == null ? null : GetGroupForHandle(window.Handle);

        public TaskGroup GetGroupForHandle(IntPtr handle)
            => _taskGroups.FirstOrDefault(g => g.Handles.Contains(handle));

        // The live instance currently in the source collection for the given HWND, or null if the
        // window is absent right now (hidden on another workspace, or genuinely gone).
        private ApplicationWindow ResolveWindow(IntPtr handle)
            => _source?.FirstOrDefault(w => w.Handle == handle);

        // The group's members that currently exist in the source collection, as live instances.
        // Members hidden on another workspace resolve to nothing and are omitted.
        public List<ApplicationWindow> GetLiveWindows(TaskGroup group)
        {
            var result = new List<ApplicationWindow>();
            if (_source == null) return result;

            foreach (var w in _source)
            {
                if (group.Handles.Contains(w.Handle))
                    result.Add(w);
            }
            return result;
        }

        private static int IndexOfHandle(ObservableCollection<ApplicationWindow> source, IntPtr handle)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].Handle == handle)
                    return i;
            }
            return -1;
        }

        // One-line description of a group for diagnostics: each member's hwnd plus whether it
        // currently resolves to a live instance (and its title if so).
        public string DescribeGroup(TaskGroup group)
        {
            var members = group.Handles.Select(h =>
            {
                var w = ResolveWindow(h);
                return w == null
                    ? $"0x{h.ToInt64():X}=<not live>"
                    : $"0x{h.ToInt64():X}=\"{w.Title}\"";
            });
            return $"[collapsed={group.IsCollapsed} members({group.Handles.Count}): {string.Join(", ", members)}]";
        }

        // Dumps every group's membership - called around workspace switches to diagnose groups
        // getting lost.
        public void LogGroups(string context)
        {
            ShellLogger.Debug($"TaskGroupManager: {context}: {_taskGroups.Count} group(s)");
            foreach (var g in _taskGroups)
                ShellLogger.Debug($"TaskGroupManager:   {DescribeGroup(g)}");
        }

        // internal so TaskList can look up a group member's button to run slide animations.
        internal static TaskButton GetTaskButton(ContentPresenter cp)
        {
            if (cp == null || System.Windows.Media.VisualTreeHelper.GetChildrenCount(cp) == 0) return null;
            return System.Windows.Media.VisualTreeHelper.GetChild(cp, 0) as TaskButton;
        }

        // Schedules a visual refresh on the next layout pass so containers are ready.
        public void UpdateGroupVisuals()
            => _dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)ApplyGroupVisuals);

        public void ApplyGroupVisuals()
        {
            for (int i = 0; i < _tasksList.Items.Count; i++)
            {
                if (_tasksList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
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

                if (_provisionalGroup?.Handles.Contains(window.Handle) == true)
                {
                    btn.SetGroupColor(_provisionalGroup.GroupColor);
                    continue;
                }

                btn.SetGroupColor(null);
            }
        }

        // Live preview during a solo drag: shows/hides the dragged button's group-color border as
        // it crosses in and out of its original group's span, ahead of the drag actually finishing.
        public void SetSoloDragVisual(ContentPresenter cp, TaskGroup originalGroup, bool inGroup)
        {
            var btn = GetTaskButton(cp);
            btn?.SetGroupColor(inGroup ? originalGroup.GroupColor : (Color?)null);
        }

        // Called by TaskButton.Loaded so new buttons pick up their group color.
        public void RefreshGroupVisual(TaskButton btn)
        {
            if (btn?.DataContext is not ApplicationWindow window) return;
            var group = GetGroupForWindow(window);
            if (group != null)
                btn.SetGroupColor(group.GroupColor);
            else if (_provisionalGroup?.Handles.Contains(window.Handle) == true)
                btn.SetGroupColor(_provisionalGroup.GroupColor);
            else
                btn.SetGroupColor(null);
        }

        public void UngroupWindow(ApplicationWindow window, ObservableCollection<ApplicationWindow> source)
        {
            if (window == null) return;
            var group = GetGroupForWindow(window);
            if (group == null) return;

            ShellLogger.Debug($"TaskGroupManager: UngroupWindow 0x{window.Handle.ToInt64():X} \"{window.Title}\" from {DescribeGroup(group)}");
            group.Handles.Remove(window.Handle);

            if (source != null)
            {
                int windowPos = source.IndexOf(window);
                if (windowPos >= 0 && group.Handles.Count > 0)
                {
                    // Find the span of remaining group members in collection order.
                    var groupPositions = group.Handles
                        .Select(h => IndexOfHandle(source, h))
                        .Where(idx => idx >= 0)
                        .OrderBy(idx => idx)
                        .ToList();

                    if (groupPositions.Count > 0)
                    {
                        int firstGroupIdx = groupPositions[0];
                        int lastGroupIdx = groupPositions[groupPositions.Count - 1];

                        // Already touching the group on one side (e.g. the group shrank to a
                        // single member sitting right next to this window) - leave it where it
                        // is. Without this, a lone remaining member makes firstGroupIdx equal
                        // lastGroupIdx, so the "closer end" distances below always tie and the
                        // tie-break would otherwise yank an already-adjacent window to the other
                        // side, swapping the two buttons for no reason.
                        bool alreadyAdjacent = windowPos == firstGroupIdx - 1 || windowPos == lastGroupIdx + 1;

                        if (!alreadyAdjacent)
                        {
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
            }

            if (group.Handles.Count <= 1)
                _taskGroups.Remove(group);

            UpdateGroupVisuals();
        }

        public void ChangeGroupColor(ApplicationWindow window)
        {
            var group = GetGroupForWindow(window);
            if (group == null) return;
            group.GroupColor = TaskGroup.RandomColor();
            UpdateGroupVisuals();
        }

        // Dissolves group membership for all windows without repositioning them.
        public void RemoveGroup(ApplicationWindow window)
        {
            if (window == null) return;
            var group = GetGroupForWindow(window);
            if (group == null) return;
            ShellLogger.Debug($"TaskGroupManager: RemoveGroup via 0x{window.Handle.ToInt64():X} \"{window.Title}\": {DescribeGroup(group)}");
            group.ClearTiled();
            _taskGroups.Remove(group);
            UpdateGroupVisuals();
        }

        // Returns the live windows belonging to the same group as the given window. Members
        // currently hidden on another workspace have no live instance and are omitted.
        public List<ApplicationWindow> GetGroupWindows(ApplicationWindow window)
        {
            var group = GetGroupForWindow(window);
            if (group == null) return new List<ApplicationWindow> { window };
            return GetLiveWindows(group);
        }

        // Same as GetGroupWindows, but ordered by each window's position in the taskbar so
        // callers (e.g. tiling) can assign positions left-to-right consistently.
        public List<ApplicationWindow> GetGroupWindowsOrdered(ApplicationWindow window)
        {
            var group = GetGroupForWindow(window);
            if (group == null) return new List<ApplicationWindow> { window };

            // GetLiveWindows walks the source collection in order, so the result is already
            // sorted by taskbar position.
            return GetLiveWindows(group);
        }

        public void CollapseGroup(ApplicationWindow window)
        {
            if (window == null) return;
            var group = GetGroupForWindow(window);
            if (group == null) return;
            group.IsCollapsed = true;
            UpdateGroupVisuals();
        }

        // Collapsing hides every member but one - if one of them is the active window, collapsing
        // would hide the very button the user is currently working with, so leave the whole group
        // expanded until none of its windows is active anymore.
        public bool GroupHasActiveWindow(TaskGroup group)
            => GetLiveWindows(group).Any(w => w.State == ApplicationWindow.WindowState.Active);

        // The single visible button for a collapsed group: whichever live member sits leftmost
        // (earliest) in the underlying source collection. Always a live instance from the
        // collection (callers compare it by reference against filter candidates), or null when no
        // member is currently present at all (e.g. the whole group is on another workspace).
        public ApplicationWindow GetCollapsedRepresentative(TaskGroup group, ObservableCollection<ApplicationWindow> source)
        {
            if (source == null) return group.Handles.Select(ResolveWindow).FirstOrDefault(w => w != null);

            foreach (var w in source)
            {
                if (group.Handles.Contains(w.Handle))
                    return w;
            }
            return null;
        }

        public void CollapseAllGroups()
        {
            foreach (var group in _taskGroups)
                group.IsCollapsed = true;
            UpdateGroupVisuals();
        }

        public void UncollapseAllGroups()
        {
            foreach (var group in _taskGroups)
                group.IsCollapsed = false;
            UpdateGroupVisuals();
        }

        // Removes windows from their groups (dissolving any group left with <=1 member).
        // Returns whether any group was touched. Used when the source collection reports a Remove.
        public bool RemoveWindows(IEnumerable<ApplicationWindow> windows)
        {
            bool changed = false;
            foreach (var window in windows)
            {
                var group = GetGroupForWindow(window);
                if (group == null) continue;
                ShellLogger.Debug($"TaskGroupManager: RemoveWindows removing 0x{window.Handle.ToInt64():X} \"{window.Title}\" from {DescribeGroup(group)}");
                group.Handles.Remove(window.Handle);
                group.ClearTiled();
                if (group.Handles.Count <= 1)
                {
                    ShellLogger.Debug($"TaskGroupManager: RemoveWindows dissolving group left with {group.Handles.Count} member(s)");
                    _taskGroups.Remove(group);
                }
                changed = true;
            }
            return changed;
        }

        // Dissolves group membership for any window no longer present in the source collection.
        // Used when the source collection reports a full Reset. keepHidden lets a caller preserve
        // membership for windows that are legitimately absent from the source collection right now
        // (e.g. hidden by WorkspaceManager for being on another workspace) rather than genuinely gone.
        public bool ReconcileWithSource(IEnumerable<ApplicationWindow> remainingWindows, Func<IntPtr, bool> keepHidden = null)
        {
            bool changed = false;
            var remaining = new HashSet<IntPtr>(remainingWindows.Select(w => w.Handle));
            foreach (var g in _taskGroups.ToList())
            {
                var removedHandles = g.Handles.Where(h => !remaining.Contains(h) && keepHidden?.Invoke(h) != true).ToList();
                if (removedHandles.Count > 0)
                {
                    ShellLogger.Debug($"TaskGroupManager: ReconcileWithSource removing {string.Join(", ", removedHandles.Select(h => $"0x{h.ToInt64():X}"))} from {DescribeGroup(g)}");
                    g.Handles.RemoveAll(removedHandles.Contains);
                    g.ClearTiled();
                    changed = true;
                }
                if (g.Handles.Count <= 1)
                {
                    ShellLogger.Debug($"TaskGroupManager: ReconcileWithSource dissolving group left with {g.Handles.Count} member(s): {DescribeGroup(g)}");
                    _taskGroups.Remove(g);
                    changed = true;
                }
            }
            return changed;
        }

        // A grouped window just cloaked while still existing (not genuinely closed) - it may have
        // been moved to a different virtual desktop individually, or this may be one event in a
        // bulk desktop switch that's taking its whole group along together. Debounce briefly so
        // any sibling cloak/uncloak events from the same switch have time to land before deciding.
        public void ScheduleDesktopSplitCheck(ObservableCollection<ApplicationWindow> source)
        {
            _desktopSplitTimer?.Stop();
            _desktopSplitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DesktopSplitCheckMs) };
            _desktopSplitTimer.Tick += (_, _) =>
            {
                _desktopSplitTimer.Stop();
                _desktopSplitTimer = null;
                CheckForDesktopSplits(source);
            };
            _desktopSplitTimer.Start();
        }

        // A group has split across virtual desktops if some (but not all) of its members are
        // currently visible. Ungroup just the members that left - the least surprising outcome,
        // since the user's action (moving one window) only affected that window, not its
        // groupmates. A group where every member is invisible together (its whole desktop was
        // switched away from) is left untouched; it resurfaces intact once that desktop returns.
        // Only live members are considered: a member with no instance at all (hidden by our own
        // WorkspaceManager) is on another workspace, not another virtual desktop, and must not
        // trigger a split.
        private void CheckForDesktopSplits(ObservableCollection<ApplicationWindow> source)
        {
            foreach (var group in _taskGroups.ToList())
            {
                var live = GetLiveWindows(group);
                var invisible = live.Where(w => !w.ShowInTaskbar).ToList();
                if (invisible.Count == 0 || invisible.Count == live.Count)
                    continue;

                ShellLogger.Debug($"TaskGroupManager: CheckForDesktopSplits ungrouping {invisible.Count} invisible of {live.Count} live member(s) in {DescribeGroup(group)}");
                foreach (var w in invisible)
                    UngroupWindow(w, source);
            }
        }

        public void StartHover(int targetIndex, Action onConfirmed)
        {
            _groupHoverTargetIndex = targetIndex;
            _groupHoverTimer?.Stop();
            _groupHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GroupHoverMs) };
            _groupHoverTimer.Tick += (_, _) =>
            {
                _groupHoverTimer.Stop();
                _groupHoverTimer = null;
                onConfirmed();
            };
            _groupHoverTimer.Start();
        }

        public void CancelHover()
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

        // Builds the provisional (drag-preview) group merging the dragged and target windows'
        // existing groups, so ApplyGroupVisuals can show the merge before it's committed.
        public void ConfirmHover(ApplicationWindow draggedWindow, ApplicationWindow targetWindow)
        {
            var draggedGroup = GetGroupForWindow(draggedWindow);
            var targetGroup = GetGroupForWindow(targetWindow);

            // Pick the color from whichever side already has a group; otherwise random.
            Color color = (targetGroup ?? draggedGroup)?.GroupColor ?? TaskGroup.RandomColor();

            _provisionalGroup = new TaskGroup(color)
            {
                // If either side being merged was collapsed, keep the merged group collapsed -
                // otherwise dragging a new window onto a collapsed group would silently
                // uncollapse it.
                IsCollapsed = (draggedGroup?.IsCollapsed ?? false) || (targetGroup?.IsCollapsed ?? false)
            };

            // Merge both sides into the provisional display set.
            if (draggedGroup != null)
                foreach (var h in draggedGroup.Handles) _provisionalGroup.Handles.Add(h);
            else
                _provisionalGroup.Handles.Add(draggedWindow.Handle);

            if (targetGroup != null)
            {
                foreach (var h in targetGroup.Handles)
                    if (!_provisionalGroup.Handles.Contains(h)) _provisionalGroup.Handles.Add(h);
            }
            else
            {
                if (!_provisionalGroup.Handles.Contains(targetWindow.Handle))
                    _provisionalGroup.Handles.Add(targetWindow.Handle);
            }

            _groupHoverConfirmed = true;
            ApplyGroupVisuals();
        }

        // Commits the provisional group (built by ConfirmHover) as a real group, stripping its
        // members from whatever groups they previously belonged to.
        public void CommitProvisionalGroup()
        {
            if (!_groupHoverConfirmed || _provisionalGroup == null) return;

            // Collect old groups that contain any provisional member.
            var oldGroups = new HashSet<TaskGroup>();
            foreach (var h in _provisionalGroup.Handles)
            {
                var g = GetGroupForHandle(h);
                if (g != null) oldGroups.Add(g);
            }

            // Strip those windows from their old groups (may dissolve them).
            foreach (var g in oldGroups)
            {
                g.Handles.RemoveAll(h => _provisionalGroup.Handles.Contains(h));
                g.ClearTiled();
                if (g.Handles.Count <= 1)
                    _taskGroups.Remove(g);
            }

            // The provisional object becomes the committed group.
            ShellLogger.Debug($"TaskGroupManager: CommitProvisionalGroup {DescribeGroup(_provisionalGroup)}");
            _taskGroups.Add(_provisionalGroup);
            _provisionalGroup = null;
            _groupHoverConfirmed = false;
            _groupHoverTargetIndex = -1;
        }

        // Called after a ctrl+drag of a single button that started out belonging to a multi-member
        // group finishes reordering the taskbar (and didn't merge into a different group via
        // hover-confirm - that path already resolves membership itself). stillInGroup is the same
        // live in/out-of-group state that was shown to the user during the drag (see
        // TaskList's _dragSoloInGroup), so the committed membership never contradicts the preview.
        public void ResolveSoloGroupDrag(ApplicationWindow window, TaskGroup originalGroup, bool stillInGroup)
        {
            if (window == null || originalGroup == null) return;
            if (!_taskGroups.Contains(originalGroup) || !originalGroup.Handles.Contains(window.Handle)) return;

            if (!stillInGroup)
            {
                ShellLogger.Debug($"TaskGroupManager: ResolveSoloGroupDrag removing 0x{window.Handle.ToInt64():X} \"{window.Title}\" from {DescribeGroup(originalGroup)}");
                originalGroup.Handles.Remove(window.Handle);
                originalGroup.ClearTiled();
                if (originalGroup.Handles.Count <= 1)
                    _taskGroups.Remove(originalGroup);
            }

            UpdateGroupVisuals();
        }
    }
}
