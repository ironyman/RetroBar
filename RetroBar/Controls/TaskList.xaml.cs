using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    }
}