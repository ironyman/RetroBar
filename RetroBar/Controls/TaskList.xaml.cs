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

                isLoaded = true;
            }
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

            isLoaded = false;
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();
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

            // Step the gap one slot at a time. A swap triggers once the dragged button's leading
            // edge (in the direction of travel) passes the midpoint of the neighbouring button as
            // it is currently displayed, which is symmetric for both directions.
            double draggedCenter = MainCoord(_dragSlots[_dragFromIndex], horizontal)
                                   + MainSize(_dragSizes[_dragFromIndex], horizontal) / 2
                                   + (horizontal ? delta.X : delta.Y);
            double half = MainSize(_dragSizes[_dragFromIndex], horizontal) / 2;

            int to = _dragToIndex;
            int count = _dragContainers.Count;
            while (to < count - 1 && draggedCenter + half > SlotCenter(to + 1, horizontal)) to++;
            while (to > 0 && draggedCenter - half < SlotCenter(to - 1, horizontal)) to--;

            if (to != _dragToIndex)
            {
                _dragToIndex = to;
                LayoutDragSiblings();
            }
        }

        private double SlotCenter(int slot, bool horizontal)
            => MainCoord(_dragSlots[slot], horizontal) + MainSize(_dragSizes[slot], horizontal) / 2;

        public void EndButtonDrag()
        {
            if (!_isDragging)
                return;

            // Stop tracking the cursor immediately so a stray mouse move during the settle
            // animation can't restart UpdateButtonDrag and cancel the snap (leaving it stuck).
            _isDragging = false;

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
        }

        private void LayoutDragSiblings()
        {
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

            if (to != from && taskbarItems?.SourceCollection is ObservableCollection<ApplicationWindow> source
                && _dragContainer.DataContext is ApplicationWindow dragged)
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