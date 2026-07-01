using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;
using RetroBar.Extensions;
using RetroBar.Utilities;
using TrayIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace RetroBar.Controls
{
    public partial class NotifyIconList : UserControl
    {
        private bool _isLoaded;
        private CollectionViewSource pinnedNotifyIconsSource;
        private ListCollectionView _allUserIcons;
        private ListCollectionView _pinnedUserIcons;
        private ObservableCollection<ManagedShell.WindowsTray.NotifyIcon> promotedIcons = [];
        private ObservableCollection<object> _displayItems = [];

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

        private void NotifyIconList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape || _winBSavedForeground == IntPtr.Zero) return;

            RestoreSavedForeground();
            e.Handled = true;
        }

        private void RestoreSavedForeground()
        {
            var hwndToRestore = _winBSavedForeground;
            _winBSavedForeground = IntPtr.Zero;
            ForceForeground(hwndToRestore);
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
                RestoreSavedForeground();
                return;
            }

            _winBSavedForeground = currentFg;
            ForceForeground(taskbarHwnd);

            // WPF focus calls must be posted so they run after Win32 focus has settled.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (NotifyIconToggleButton.Visibility == Visibility.Visible)
                {
                    NotifyIconToggleButton.Tag = "HotkeyFocus";
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

        public NotifyIconList()
        {
            InitializeComponent();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.NotifyIconOrderPinned) ||
                e.PropertyName == nameof(Settings.NotifyIconOrderHide) ||
                e.PropertyName == nameof(Settings.NotifyIconBehaviors))
            {
                // Refreshing here tears down and regenerates every icon container, which drops
                // the mouse capture a live drag is holding and ends it prematurely. Defer until
                // the drag commits (see FlushPendingIconListUpdates).
                if (_isIconDragging)
                {
                    _pendingCollectionRefresh = true;
                    _pendingDisplayItemsRebuild = true;
                    return;
                }

                _allUserIcons?.Refresh();
                _pinnedUserIcons?.Refresh();
                SetToggleVisibility();
                RebuildDisplayItems();
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
                    NotifyIcons.ItemsSource = _displayItems;
                }
            }
            else if (e.PropertyName == nameof(Settings.InvertIconsMode) || e.PropertyName == nameof(Settings.InvertNotifyIcons))
            {
                NotifyIcons.ItemsSource = null;
                if (Settings.Instance.CollapseNotifyIcons && NotifyIconToggleButton.IsChecked != true)
                    NotifyIcons.ItemsSource = pinnedNotifyIconsSource.View;
                else
                    NotifyIcons.ItemsSource = _displayItems;
            }
        }

        private void SetNotificationAreaCollections()
        {
            if (!_isLoaded && NotificationArea != null)
            {
                var trayIcons = (NotificationArea.PinnedIcons as ListCollectionView)?.SourceCollection as System.Collections.IList;

                // Expanded view sorts by [hide-side icons, pinned-side icons].
                var allComparer = new NotifyIconOrderComparer(trayIcons, () =>
                {
                    var combined = new List<string>(Settings.Instance.NotifyIconOrderHide);
                    combined.AddRange(Settings.Instance.NotifyIconOrderPinned);
                    return combined;
                });

                // Collapsed view sorts by pinned-side only.
                var pinnedComparer = new NotifyIconOrderComparer(trayIcons,
                    () => Settings.Instance.NotifyIconOrderPinned);

                _allUserIcons = new ListCollectionView(trayIcons);
                _allUserIcons.Filter = AllUserIconsFilter;
                _allUserIcons.CustomSort = allComparer;
                var liveAll = _allUserIcons as ICollectionViewLiveShaping;
                liveAll.IsLiveFiltering = true;
                liveAll.LiveFilteringProperties.Add("IsHidden");
                liveAll.LiveFilteringProperties.Add("IsPinned");

                _pinnedUserIcons = new ListCollectionView(trayIcons);
                _pinnedUserIcons.Filter = PinnedUserIconsFilter;
                _pinnedUserIcons.CustomSort = pinnedComparer;
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

                MigrateAndEnsureIconOrders();
                RebuildDisplayItems();

                if (Settings.Instance.CollapseNotifyIcons)
                {
                    NotifyIcons.ItemsSource = pinnedNotifyIconsSource.View;
                    SetToggleVisibility();
                    if (NotifyIconToggleButton.IsChecked == true)
                        NotifyIconToggleButton.IsChecked = false;
                }
                else
                {
                    NotifyIcons.ItemsSource = _displayItems;
                }

                _isLoaded = true;
            }
        }

        private void MigrateAndEnsureIconOrders()
        {
            var oldOrder = Settings.Instance.NotifyIconOrder;
            if (oldOrder.Count > 0 &&
                Settings.Instance.NotifyIconOrderPinned.Count == 0 &&
                Settings.Instance.NotifyIconOrderHide.Count == 0)
            {
                int sepIdx = oldOrder.IndexOf(SeparatorPlaceholder.SentinelId);
                if (sepIdx >= 0)
                {
                    Settings.Instance.NotifyIconOrderHide = oldOrder.Take(sepIdx).ToList();
                    Settings.Instance.NotifyIconOrderPinned = oldOrder.Skip(sepIdx + 1).ToList();
                }
                else
                {
                    Settings.Instance.NotifyIconOrderPinned = new List<string>(oldOrder);
                }
                Settings.Instance.NotifyIconOrder = new List<string>();
            }

            // Older builds stored full identifiers that embedded the icon's title. Titles change
            // at runtime (e.g. Steam's download progress), so those entries went stale and bred
            // duplicates. Collapse every stored identifier to its stable form and de-duplicate.
            Settings.Instance.NotifyIconOrderHide = StabilizeIdentifierList(Settings.Instance.NotifyIconOrderHide);
            Settings.Instance.NotifyIconOrderPinned = StabilizeIdentifierList(Settings.Instance.NotifyIconOrderPinned);

            var behaviors = Settings.Instance.NotifyIconBehaviors;
            var seenBehaviors = new HashSet<string>();
            var stableBehaviors = new List<NotifyIconBehaviorSetting>();
            bool behaviorsChanged = false;
            foreach (var setting in behaviors)
            {
                string stable = NotifyIconExtensions.ToStableIdentifier(setting.Identifier);
                if (stable != setting.Identifier) behaviorsChanged = true;
                if (!seenBehaviors.Add(stable)) { behaviorsChanged = true; continue; }
                stableBehaviors.Add(new NotifyIconBehaviorSetting { Identifier = stable, Behavior = setting.Behavior });
            }
            if (behaviorsChanged)
                Settings.Instance.NotifyIconBehaviors = stableBehaviors;
        }

        private static List<string> StabilizeIdentifierList(List<string> stored)
        {
            var seen = new HashSet<string>();
            var result = new List<string>(stored.Count);
            foreach (var entry in stored)
            {
                string stable = NotifyIconExtensions.ToStableIdentifier(entry);
                if (seen.Add(stable)) result.Add(stable);
            }
            return result;
        }

        private void RebuildDisplayItems()
        {
            _displayItems.Clear();

            // Two passes grouped by behavior: new icons not yet in any order list sort to the end
            // of _allUserIcons, so a single-pass separator insertion would put them on the wrong
            // side. HideWhenInactive icons go before the separator, everything else after.
            foreach (TrayIcon icon in _allUserIcons)
            {
                if (icon.GetBehavior() == NotifyIconBehavior.HideWhenInactive)
                    _displayItems.Add(icon);
            }

            _displayItems.Add(SeparatorPlaceholder.Instance);

            foreach (TrayIcon icon in _allUserIcons)
            {
                if (icon.GetBehavior() != NotifyIconBehavior.HideWhenInactive)
                    _displayItems.Add(icon);
            }
        }

        private static void NotificationAreaChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is NotifyIconList notifyIconList && e.OldValue == null && e.NewValue != null)
                notifyIconList.SetNotificationAreaCollections();
        }

        private bool AllUserIconsFilter(object obj)
        {
            if (obj is TrayIcon icon)
            {
                if (icon.IsSystemIcon()) return false;
                var behavior = icon.GetBehavior();
                if (behavior == NotifyIconBehavior.Remove) return false;
                if (behavior == NotifyIconBehavior.AlwaysHide) return false;
                if (icon.IsHidden)
                {
                    // Show OS-hidden icons only when they have an explicit position in one of the order lists.
                    string id = icon.GetStableIdentifier();
                    return Settings.Instance.NotifyIconOrderHide.Contains(id) ||
                           Settings.Instance.NotifyIconOrderPinned.Contains(id);
                }
                return true;
            }
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

            var window = Window.GetWindow(this);
            window?.AddHandler(System.Windows.Input.Mouse.PreviewMouseDownEvent,
                new System.Windows.Input.MouseButtonEventHandler(OnWindowPreviewMouseDown), true);
        }

        private void NotifyIconList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            window?.RemoveHandler(System.Windows.Input.Mouse.PreviewMouseDownEvent,
                new System.Windows.Input.MouseButtonEventHandler(OnWindowPreviewMouseDown));

            if (Host?.hotkeyManager != null)
                Host.hotkeyManager.FocusTrayHotkeyPressed -= OnFocusTrayHotkeyPressed;

            if (!_isLoaded) return;

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

            if (NotificationArea != null)
                NotificationArea.NotificationBalloonShown -= NotificationArea_NotificationBalloonShown;

            if (_allUserIcons != null)
                ((INotifyCollectionChanged)_allUserIcons).CollectionChanged -= AllUserIcons_CollectionChanged;

            _isLoaded = false;
        }

        // Any mouse click anywhere suppresses the hotkey-only focus visual, mirroring
        // WPF's normal "no focus rect after a mouse click" convention.
        private void OnWindowPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            NotifyIconToggleButton.Tag = null;
        }

        private void AllUserIcons_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Same reasoning as Settings_PropertyChanged: don't tear down containers
                // out from under an in-progress drag.
                if (_isIconDragging)
                {
                    _pendingDisplayItemsRebuild = true;
                    return;
                }

                SetToggleVisibility();
                RebuildDisplayItems();
            }));
        }

        private void NotifyIconToggleButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShellFlyoutHelper.DismissIfActive();
            if (NotifyIconToggleButton.IsChecked == true)
                NotifyIcons.ItemsSource = _displayItems;
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

        #region Live drag reorder

        private const double IconDragAnimationMs = 180;
        private const double IconDragSnapMs = 200;

        private bool _isIconDragging;
        private ContentPresenter _iconDragContainer;
        private WrapPanel _iconDragPanel;
        private Point _iconDragStartPanelPoint;
        private List<ContentPresenter> _iconDragContainers;
        private List<Point> _iconDragSlots;
        private List<Size> _iconDragSizes;
        private int _iconDragFromIndex;
        private int _iconDragToIndex;
        private readonly Dictionary<ContentPresenter, Point> _iconDragSiblingTargets = new();
        private TrayIcon _iconBeingDragged;
        private bool _pendingCollectionRefresh;
        private bool _pendingDisplayItemsRebuild;

        public bool IsDraggingIcon => _isIconDragging;

        public void StartIconDrag(FrameworkElement draggedControl, MouseEventArgs e)
        {
            if (_isIconDragging || draggedControl == null || NotifyIcons.Items.Count < 2)
                return;

            _iconDragPanel = FindVisualChild<WrapPanel>(NotifyIcons);
            if (_iconDragPanel == null)
                return;

            _iconDragContainers = new List<ContentPresenter>();
            _iconDragSlots = new List<Point>();
            _iconDragSizes = new List<Size>();
            _iconDragFromIndex = -1;

            for (int i = 0; i < NotifyIcons.Items.Count; i++)
            {
                if (NotifyIcons.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp)
                    return;

                cp.RenderTransform = null;
                _iconDragContainers.Add(cp);
                _iconDragSlots.Add(cp.TranslatePoint(new Point(0, 0), _iconDragPanel));
                _iconDragSizes.Add(new Size(cp.ActualWidth, cp.ActualHeight));

                if (ReferenceEquals(cp.DataContext, draggedControl.DataContext))
                    _iconDragFromIndex = i;
            }

            if (_iconDragFromIndex < 0)
                return;

            _isIconDragging = true;
            _iconBeingDragged = _iconDragContainers[_iconDragFromIndex].DataContext as TrayIcon;
            _iconDragContainer = _iconDragContainers[_iconDragFromIndex];
            _iconDragToIndex = _iconDragFromIndex;
            _iconDragStartPanelPoint = e.GetPosition(_iconDragPanel);
            _iconDragSiblingTargets.Clear();

            Panel.SetZIndex(_iconDragContainer, 100);
        }

        public void UpdateIconDrag(MouseEventArgs e)
        {
            if (!_isIconDragging)
                return;

            // Use the WrapPanel's own flow orientation, not the taskbar's screen-edge orientation:
            // with RowCount > 1 on a horizontal taskbar, IconListOrientation flips the panel to
            // flow vertically (columns) even though Host.Orientation stays Horizontal.
            bool horizontal = _iconDragPanel.Orientation == System.Windows.Controls.Orientation.Horizontal;
            Vector delta = e.GetPosition(_iconDragPanel) - _iconDragStartPanelPoint;

            var draggedTransform = GetIconTranslate(_iconDragContainer);
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

            double draggedCenter = IconMainCoord(_iconDragSlots[_iconDragFromIndex], horizontal)
                                   + IconMainSize(_iconDragSizes[_iconDragFromIndex], horizontal) / 2
                                   + (horizontal ? delta.X : delta.Y);
            double half = IconMainSize(_iconDragSizes[_iconDragFromIndex], horizontal) / 2;

            int to = _iconDragToIndex;
            int count = _iconDragContainers.Count;
            while (to < count - 1 && draggedCenter + half > IconSlotCenter(to + 1, horizontal)) to++;
            while (to > 0 && draggedCenter - half < IconSlotCenter(to - 1, horizontal)) to--;

            if (to != _iconDragToIndex)
            {
                _iconDragToIndex = to;
                LayoutIconDragSiblings();
            }
        }

        private double IconSlotCenter(int slot, bool horizontal)
            => IconMainCoord(_iconDragSlots[slot], horizontal) + IconMainSize(_iconDragSizes[slot], horizontal) / 2;

        public void EndIconDrag()
        {
            if (!_isIconDragging)
                return;

            _isIconDragging = false;

            var taskbar = Window.GetWindow(this);
            bool droppedOutside = taskbar != null && IsOutsideTaskbarWindow(taskbar);

            Vector finalOffset = _iconDragSlots[_iconDragToIndex] - _iconDragSlots[_iconDragFromIndex];
            var transform = GetIconTranslate(_iconDragContainer);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);

            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            var animX = new DoubleAnimation(transform.X, finalOffset.X, TimeSpan.FromMilliseconds(IconDragSnapMs)) { EasingFunction = ease };
            var animY = new DoubleAnimation(transform.Y, finalOffset.Y, TimeSpan.FromMilliseconds(IconDragSnapMs)) { EasingFunction = ease };
            animX.Completed += (_, _) => CommitIconDrag(droppedOutside);
            transform.BeginAnimation(TranslateTransform.XProperty, animX);
            transform.BeginAnimation(TranslateTransform.YProperty, animY);
        }

        private void LayoutIconDragSiblings()
        {
            var order = new List<int>(_iconDragContainers.Count);
            for (int i = 0; i < _iconDragContainers.Count; i++)
            {
                if (i != _iconDragFromIndex) order.Add(i);
            }
            order.Insert(_iconDragToIndex, _iconDragFromIndex);

            for (int pos = 0; pos < order.Count; pos++)
            {
                int idx = order[pos];
                if (idx == _iconDragFromIndex) continue;

                Vector offset = _iconDragSlots[pos] - _iconDragSlots[idx];
                AnimateIconSibling(_iconDragContainers[idx], offset);
            }
        }

        private void AnimateIconSibling(ContentPresenter cp, Vector offset)
        {
            Point target = new Point(offset.X, offset.Y);
            if (_iconDragSiblingTargets.TryGetValue(cp, out Point current) && current == target)
                return;

            _iconDragSiblingTargets[cp] = target;

            var transform = GetIconTranslate(cp);
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(offset.X, TimeSpan.FromMilliseconds(IconDragAnimationMs)) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(offset.Y, TimeSpan.FromMilliseconds(IconDragAnimationMs)) { EasingFunction = ease });
        }

        private void CommitIconDrag(bool droppedOutside)
        {
            if (_iconDragContainers == null)
                return;

            int from = _iconDragFromIndex;
            int to = _iconDragToIndex;
            var draggedIcon = _iconBeingDragged;
            bool isDraggingSeparator = _iconDragContainer.DataContext is SeparatorPlaceholder;

            // Build the new visual order once — used in both branches below.
            var order = new List<int>(_iconDragContainers.Count);
            for (int i = 0; i < _iconDragContainers.Count; i++)
                if (i != from) order.Add(i);
            order.Insert(to, from);

            // Locate the separator in the new visual order.
            int sepContainerIdx = _iconDragContainers.FindIndex(cp => cp.DataContext is SeparatorPlaceholder);
            int sepNewVisualPos = sepContainerIdx >= 0 ? order.IndexOf(sepContainerIdx) : -1;

            if (droppedOutside && draggedIcon != null)
            {
                draggedIcon.SetBehavior(NotifyIconBehavior.HideWhenInactive);
            }
            else if (isDraggingSeparator)
            {
                // ── Separator was dragged ──────────────────────────────────────────
                // Rebuild both lists from the new visual order, preserving the order
                // of every icon that moved past the separator.
                var newHideOrder = new List<string>();
                var newPinnedOrder = new List<string>();
                var toPin = new List<TrayIcon>();
                var toUnpin = new List<TrayIcon>();

                for (int pos = 0; pos < order.Count; pos++)
                {
                    if (_iconDragContainers[order[pos]].DataContext is not TrayIcon icon) continue;
                    bool onPinnedSide = pos > to;
                    if (onPinnedSide) newPinnedOrder.Add(icon.GetStableIdentifier());
                    else newHideOrder.Add(icon.GetStableIdentifier());

                    if (onPinnedSide && !icon.IsPinned) toPin.Add(icon);
                    else if (!onPinnedSide && icon.IsPinned) toUnpin.Add(icon);
                }

                // Write orders first so any rebuild triggered by SetBehavior below
                // already sees the correct new lists.
                Settings.Instance.NotifyIconOrderHide = newHideOrder;
                Settings.Instance.NotifyIconOrderPinned = newPinnedOrder;

                foreach (var icon in toPin) icon.SetBehavior(NotifyIconBehavior.AlwaysShow);
                foreach (var icon in toUnpin) icon.SetBehavior(NotifyIconBehavior.HideWhenInactive);
            }
            else if (to != from && draggedIcon != null)
            {
                // ── Regular icon was dragged ───────────────────────────────────────
                string draggedId = draggedIcon.GetStableIdentifier();
                bool wasOnPinnedSide = Settings.Instance.NotifyIconOrderPinned.Contains(draggedId);
                bool isNowOnPinnedSide = sepNewVisualPos >= 0 ? to > sepNewVisualPos : wasOnPinnedSide;

                var hideOrder = new List<string>(Settings.Instance.NotifyIconOrderHide);
                var pinnedOrder = new List<string>(Settings.Instance.NotifyIconOrderPinned);
                var targetList = isNowOnPinnedSide ? pinnedOrder : hideOrder;

                // Find the first icon after the drop point that already belongs to the target list;
                // insert before it so the drop position is honoured within that list.
                TrayIcon neighbor = null;
                for (int pos = to + 1; pos < order.Count; pos++)
                {
                    if (_iconDragContainers[order[pos]].DataContext is TrayIcon ni &&
                        targetList.Contains(ni.GetStableIdentifier()))
                    { neighbor = ni; break; }
                }

                // Remove from whichever list currently holds the icon, then insert into target.
                hideOrder.Remove(draggedId);
                pinnedOrder.Remove(draggedId);

                if (neighbor != null)
                    targetList.Insert(targetList.IndexOf(neighbor.GetStableIdentifier()), draggedId);
                else
                    targetList.Add(draggedId);

                Settings.Instance.NotifyIconOrderHide = hideOrder;
                Settings.Instance.NotifyIconOrderPinned = pinnedOrder;

                // Update pin state if the icon crossed the separator.
                if (wasOnPinnedSide != isNowOnPinnedSide)
                    draggedIcon.SetBehavior(isNowOnPinnedSide ? NotifyIconBehavior.AlwaysShow : NotifyIconBehavior.HideWhenInactive);
            }

            foreach (var cp in _iconDragContainers)
            {
                if (cp.RenderTransform is TranslateTransform t)
                {
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                }
                cp.RenderTransform = null;
                Panel.SetZIndex(cp, 0);
            }

            _isIconDragging = false;
            _iconDragContainer = null;
            _iconDragContainers = null;
            _iconDragSlots = null;
            _iconDragSizes = null;
            _iconDragPanel = null;
            _iconDragSiblingTargets.Clear();
            _iconBeingDragged = null;

            FlushPendingIconListUpdates();
        }

        // Runs any collection refresh/rebuild that was deferred while a drag was in progress
        // (see Settings_PropertyChanged / AllUserIcons_CollectionChanged).
        private void FlushPendingIconListUpdates()
        {
            if (_pendingCollectionRefresh)
            {
                _pendingCollectionRefresh = false;
                _allUserIcons?.Refresh();
                _pinnedUserIcons?.Refresh();
            }

            if (_pendingDisplayItemsRebuild)
            {
                _pendingDisplayItemsRebuild = false;
                SetToggleVisibility();
                RebuildDisplayItems();
            }
        }

        private static bool IsOutsideTaskbarWindow(Window window)
        {
            var topLeft = window.PointToScreen(new Point(0, 0));
            var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            var mousePos = System.Windows.Forms.Cursor.Position;

            return mousePos.X < topLeft.X || mousePos.X > bottomRight.X
                || mousePos.Y < topLeft.Y || mousePos.Y > bottomRight.Y;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static TranslateTransform GetIconTranslate(ContentPresenter cp)
        {
            if (cp.RenderTransform is TranslateTransform t)
                return t;

            var transform = new TranslateTransform();
            cp.RenderTransform = transform;
            return transform;
        }

        private static double IconMainCoord(Point p, bool horizontal) => horizontal ? p.X : p.Y;
        private static double IconMainSize(Size s, bool horizontal) => horizontal ? s.Width : s.Height;

        #endregion
    }
}
