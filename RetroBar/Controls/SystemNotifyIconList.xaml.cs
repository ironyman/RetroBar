using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GongSolutions.Wpf.DragDrop;
using ManagedShell.WindowsTray;
using RetroBar.Extensions;
using RetroBar.Utilities;
using TrayIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace RetroBar.Controls
{
    public partial class SystemNotifyIconList : UserControl, IDropTarget
    {
        private bool _isLoaded;
        private ListCollectionView _systemIcons;

        public static DependencyProperty NotificationAreaProperty = DependencyProperty.Register(
            nameof(NotificationArea), typeof(NotificationArea), typeof(SystemNotifyIconList),
            new PropertyMetadata(NotificationAreaChangedCallback));

        public NotificationArea NotificationArea
        {
            get => (NotificationArea)GetValue(NotificationAreaProperty);
            set => SetValue(NotificationAreaProperty, value);
        }

        public SystemNotifyIconList()
        {
            InitializeComponent();
        }

        private static void NotificationAreaChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is SystemNotifyIconList list && e.OldValue == null && e.NewValue != null)
            {
                list.SetupCollections();
            }
        }

        private void SetupCollections()
        {
            if (_isLoaded || NotificationArea == null) return;

            _systemIcons = new ListCollectionView(NotificationArea.TrayIcons);
            _systemIcons.Filter = SystemIconFilter;
            _systemIcons.SortDescriptions.Add(new SortDescription("PinOrder", ListSortDirection.Ascending));
            var liveShaping = _systemIcons as ICollectionViewLiveShaping;
            liveShaping.IsLiveFiltering = true;
            liveShaping.LiveFilteringProperties.Add("IsHidden");
            liveShaping.IsLiveSorting = true;
            liveShaping.LiveSortingProperties.Add("PinOrder");

            SystemIcons.ItemsSource = _systemIcons;
            _isLoaded = true;
        }

        private static bool SystemIconFilter(object obj)
        {
            if (obj is TrayIcon icon)
                return icon.IsSystemIcon() && !icon.IsHidden;
            return false;
        }

        private void SystemNotifyIconList_Loaded(object sender, RoutedEventArgs e)
        {
            SetupCollections();
        }

        private void SystemNotifyIconList_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
        }

        // IDropTarget
        public new void DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is TrayIcon)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                dropInfo.Effects = System.Windows.DragDropEffects.Move;
            }
        }

        public new void Drop(IDropInfo dropInfo)
        {
            if (dropInfo.Data is TrayIcon icon)
            {
                NotifyIconDropHandler.DropHandledExternally = true;
                icon.SetBehavior(NotifyIconBehavior.AlwaysShow);
            }
        }

#if !NETCOREAPP3_1_OR_GREATER
        public new void DragEnter(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragEnter(dropInfo);
        }

        public new void DragLeave(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragLeave(dropInfo);
        }
#endif
    }
}
