using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;
using RetroBar.Extensions;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    public partial class NotifyIconList : UserControl
    {
        private bool _isLoaded;
        private CollectionViewSource pinnedNotifyIconsSource;
        private ListCollectionView _allUserIcons;
        private ListCollectionView _pinnedUserIcons;
        private ObservableCollection<ManagedShell.WindowsTray.NotifyIcon> promotedIcons = [];

        public static DependencyProperty HostProperty = DependencyProperty.Register(
            nameof(Host), typeof(Taskbar), typeof(NotifyIconList),
            new PropertyMetadata(HostChangedCallback));

        public Taskbar Host
        {
            get { return (Taskbar)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        private static void HostChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var list = (NotifyIconList)d;
            if (e.OldValue is Taskbar oldHost && oldHost.hotkeyManager != null)
                oldHost.hotkeyManager.FocusTrayHotkeyPressed -= list.OnFocusTrayHotkeyPressed;
            if (e.NewValue is Taskbar newHost && newHost.hotkeyManager != null)
                newHost.hotkeyManager.FocusTrayHotkeyPressed += list.OnFocusTrayHotkeyPressed;
        }

        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        // Foreground window saved when Win+B last focused the tray — used to toggle back.
        private IntPtr _winBSavedForeground = IntPtr.Zero;

        // AttachThreadInput technique: temporarily joins our thread's input to the foreground
        // window's thread, granting SetForegroundWindow permission without relying on WM_HOTKEY's
        // fleeting activation permission (which is gone by the keyboard-hook path).
        private void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == hwnd) return;
            uint fgTid = NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint myTid = GetCurrentThreadId();
            bool attached = fgTid != 0 && fgTid != myTid && AttachThreadInput(fgTid, myTid, true);
            NativeMethods.SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(fgTid, myTid, false);
        }

        private void OnFocusTrayHotkeyPressed(object sender, EventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;

            var taskbarHwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (taskbarHwnd == IntPtr.Zero) return;

            var currentFg = NativeMethods.GetForegroundWindow();

            if (currentFg == taskbarHwnd && _winBSavedForeground != IntPtr.Zero)
            {
                // Taskbar already has focus — toggle back to the saved prior window.
                var hwndToRestore = _winBSavedForeground;
                _winBSavedForeground = IntPtr.Zero;
                ForceForeground(hwndToRestore);
                return;
            }

            _winBSavedForeground = currentFg;
            ForceForeground(taskbarHwnd);

            // WPF focus calls must be posted so they run after Win32 focus has settled.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (NotifyIconToggleButton.Visibility == Visibility.Visible)
                {
                    NotifyIconToggleButton.Focus();
                    return;
                }

                var icons = FindName("NotifyIcons") as ItemsControl;
                if (icons?.Items.Count > 0)
                {
                    var container = icons.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
                    container?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
                }
            });
        }

        public static DependencyProperty NotificationAreaProperty = DependencyProperty.Register(
            nameof(NotificationArea), typeof(NotificationArea), typeof(NotifyIconList),
            new PropertyMetadata(NotificationAreaChangedCallback));

        public NotificationArea NotificationArea
        {
            get { return (NotificationArea)GetValue(NotificationAreaProperty); }
            set { SetValue(NotificationAreaProperty, value); }
        }

        public NotifyIconDropHandler DropHandler { get; }

        public NotifyIconList()
        {
            InitializeComponent();
            DropHandler = new NotifyIconDropHandler(this);
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.NotifyIconOrder) ||
                e.PropertyName == nameof(Settings.NotifyIconBehaviors))
            {
                _allUserIcons?.Refresh();
                _pinnedUserIcons?.Refresh();
                SetToggleVisibility();
            }
            else if (e.PropertyName == nameof(Settings.CollapseNotifyIcons))
            {
                if (Settings.Instance.CollapseNotifyIcons)
                {
                    NotifyIcons.ItemsSource = pinnedNotifyIconsSource.View;
                    SetToggleVisibility();
                }
                else
                {
                    NotifyIconToggleButton.IsChecked = false;
                    NotifyIconToggleButton.Visibility = Visibility.Collapsed;
                    NotifyIcons.ItemsSource = _allUserIcons;
                }
            }
            else if (e.PropertyName == nameof(Settings.InvertIconsMode) || e.PropertyName == nameof(Settings.InvertNotifyIcons))
            {
                NotifyIcons.ItemsSource = null;
                if (Settings.Instance.CollapseNotifyIcons && NotifyIconToggleButton.IsChecked != true)
                    NotifyIcons.ItemsSource = pinnedNotifyIconsSource.View;
                else
                    NotifyIcons.ItemsSource = _allUserIcons;
            }
        }

        private void SetNotificationAreaCollections()
        {
            if (!_isLoaded && NotificationArea != null)
            {
                var trayIcons = (NotificationArea.PinnedIcons as ListCollectionView)?.SourceCollection as System.Collections.IList;
                var comparer = new NotifyIconOrderComparer(trayIcons);

                _allUserIcons = new ListCollectionView(trayIcons);
                _allUserIcons.Filter = AllUserIconsFilter;
                _allUserIcons.CustomSort = comparer;
                var liveAll = _allUserIcons as ICollectionViewLiveShaping;
                liveAll.IsLiveFiltering = true;
                liveAll.LiveFilteringProperties.Add("IsHidden");
                liveAll.LiveFilteringProperties.Add("IsPinned");

                _pinnedUserIcons = new ListCollectionView(trayIcons);
                _pinnedUserIcons.Filter = PinnedUserIconsFilter;
                _pinnedUserIcons.CustomSort = comparer;
                var livePinned = _pinnedUserIcons as ICollectionViewLiveShaping;
                livePinned.IsLiveFiltering = true;
                livePinned.LiveFilteringProperties.Add("IsHidden");
                livePinned.LiveFilteringProperties.Add("IsPinned");

                CompositeCollection pinnedNotifyIcons = new CompositeCollection();
                pinnedNotifyIcons.Add(new CollectionContainer { Collection = promotedIcons });
                pinnedNotifyIcons.Add(new CollectionContainer { Collection = _pinnedUserIcons });
                pinnedNotifyIconsSource = new CollectionViewSource { Source = pinnedNotifyIcons };

                ((INotifyCollectionChanged)_allUserIcons).CollectionChanged += AllUserIcons_CollectionChanged;
                NotificationArea.NotificationBalloonShown += NotificationArea_NotificationBalloonShown;
                Settings.Instance.PropertyChanged += Settings_PropertyChanged;

                if (Settings.Instance.CollapseNotifyIcons)
                {
                    NotifyIcons.ItemsSource = pinnedNotifyIconsSource.View;
                    SetToggleVisibility();
                    if (NotifyIconToggleButton.IsChecked == true)
                        NotifyIconToggleButton.IsChecked = false;
                }
                else
                {
                    NotifyIcons.ItemsSource = _allUserIcons;
                }

                _isLoaded = true;
            }
        }

        private static void NotificationAreaChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is NotifyIconList notifyIconList && e.OldValue == null && e.NewValue != null)
                notifyIconList.SetNotificationAreaCollections();
        }

        private bool AllUserIconsFilter(object obj)
        {
            if (obj is ManagedShell.WindowsTray.NotifyIcon icon)
                return !icon.IsSystemIcon() && !icon.IsHidden && icon.GetBehavior() != NotifyIconBehavior.Remove;
            return false;
        }

        private bool PinnedUserIconsFilter(object obj)
        {
            if (obj is ManagedShell.WindowsTray.NotifyIcon icon)
                return icon.IsPinned && !icon.IsSystemIcon() && !icon.IsHidden;
            return false;
        }

        private void NotificationArea_NotificationBalloonShown(object sender, NotificationBalloonEventArgs e)
        {
            if (NotificationArea == null) return;

            ManagedShell.WindowsTray.NotifyIcon notifyIcon = e.Balloon.NotifyIcon;

            if (NotificationArea.PinnedIcons.Contains(notifyIcon)) return;
            if (notifyIcon.GetBehavior() != NotifyIconBehavior.HideWhenInactive) return;
            if (promotedIcons.Contains(notifyIcon)) return;

            promotedIcons.Add(notifyIcon);

            DispatcherTimer unpromoteTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(e.Balloon.Timeout + 500)
            };
            unpromoteTimer.Tick += (object s, EventArgs ea) =>
            {
                if (promotedIcons.Contains(notifyIcon)) promotedIcons.Remove(notifyIcon);
                unpromoteTimer.Stop();
            };
            unpromoteTimer.Start();
        }

        private void NotifyIconList_Loaded(object sender, RoutedEventArgs e)
        {
            SetNotificationAreaCollections();
        }

        private void NotifyIconList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

            if (NotificationArea != null)
                NotificationArea.NotificationBalloonShown -= NotificationArea_NotificationBalloonShown;

            if (_allUserIcons != null)
                ((INotifyCollectionChanged)_allUserIcons).CollectionChanged -= AllUserIcons_CollectionChanged;

            _isLoaded = false;
        }

        private void AllUserIcons_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(SetToggleVisibility));
        }

        private void NotifyIconToggleButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (NotifyIconToggleButton.IsChecked == true)
                NotifyIcons.ItemsSource = _allUserIcons;
            else
                NotifyIcons.ItemsSource = pinnedNotifyIconsSource.View;
        }

        private void SetToggleVisibility()
        {
            if (!Settings.Instance.CollapseNotifyIcons) return;

            bool hasUnpinned = _allUserIcons != null && _pinnedUserIcons != null
                && _allUserIcons.Count > _pinnedUserIcons.Count;

            if (!hasUnpinned)
            {
                NotifyIconToggleButton.Visibility = Visibility.Collapsed;
                if (NotifyIconToggleButton.IsChecked == true)
                    NotifyIconToggleButton.IsChecked = false;
            }
            else
            {
                NotifyIconToggleButton.Visibility = Visibility.Visible;
            }
        }
    }
}
