using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
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
