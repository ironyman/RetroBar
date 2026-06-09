using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using GongSolutions.Wpf.DragDrop;
using RetroBar.Controls;
using RetroBar.Extensions;
using TrayIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace RetroBar.Utilities
{
    public class NotifyIconDropHandler : IDragSource, IDropTarget
    {
        private readonly NotifyIconList _list;

        internal static TrayIcon CurrentlyDraggedIcon { get; private set; }
        internal static bool DropHandledExternally { get; set; }
        internal static bool IsDragging { get; private set; }

        public NotifyIconDropHandler(NotifyIconList list)
        {
            _list = list;
            _list.AddHandler(System.Windows.DragDrop.GiveFeedbackEvent,
                new GiveFeedbackEventHandler(OnDragGiveFeedback), true);
        }

        // IDragSource
        public void StartDrag(IDragInfo dragInfo)
        {
            if (dragInfo.SourceItem is TrayIcon icon)
            {
                IsDragging = true;
                CurrentlyDraggedIcon = icon;
                dragInfo.Data = icon;
                dragInfo.Effects = DragDropEffects.Move;
            }
        }

        public bool CanStartDrag(IDragInfo dragInfo)
        {
            return dragInfo.SourceItem is TrayIcon;
        }

        public void Dropped(IDropInfo dropInfo) { }

        public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo)
        {
            IsDragging = false;
            var icon = CurrentlyDraggedIcon;
            CurrentlyDraggedIcon = null;

            if (DropHandledExternally)
            {
                DropHandledExternally = false;
                return;
            }

            if (icon != null)
            {
                var taskbar = Window.GetWindow(_list);
                if (taskbar != null && IsOutsideWindow(taskbar))
                {
                    icon.SetBehavior(NotifyIconBehavior.AlwaysHide);
                }
            }
        }

        public void DragCancelled()
        {
            IsDragging = false;
        }

        public bool TryCatchOccurredException(Exception exception) => false;

        // IDropTarget
        public void DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is TrayIcon)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        public void Drop(IDropInfo dropInfo)
        {
            if (dropInfo.Data is TrayIcon draggedIcon)
            {
                var order = new List<string>(Settings.Instance.NotifyIconOrder);
                string draggedId = draggedIcon.Identifier;

                if (!order.Contains(draggedId))
                    order.Add(draggedId);
                order.Remove(draggedId);

                if (dropInfo.TargetItem is TrayIcon targetIcon)
                {
                    string targetId = targetIcon.Identifier;
                    if (!order.Contains(targetId))
                        order.Add(targetId);

                    int targetIdx = order.IndexOf(targetId);
                    if (dropInfo.InsertPosition.HasFlag(RelativeInsertPosition.AfterTargetItem))
                        targetIdx++;
                    order.Insert(Math.Min(targetIdx, order.Count), draggedId);
                }
                else
                {
                    order.Add(draggedId);
                }

                Settings.Instance.NotifyIconOrder = order;
            }
        }

#if !NETCOREAPP3_1_OR_GREATER
        public void DragEnter(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragEnter(dropInfo);
        }

        public void DragLeave(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragLeave(dropInfo);
        }
#endif

        private void OnDragGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (!IsDragging) return;

            var taskbar = Window.GetWindow(_list);
            if (taskbar == null) return;

            if (IsOutsideWindow(taskbar))
            {
                e.UseDefaultCursors = false;
                Mouse.SetCursor(Cursors.No);
                e.Handled = true;
            }
        }

        private static bool IsOutsideWindow(Window window)
        {
            var topLeft = window.PointToScreen(new Point(0, 0));
            var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            var mousePos = System.Windows.Forms.Cursor.Position;

            return mousePos.X < topLeft.X || mousePos.X > bottomRight.X
                || mousePos.Y < topLeft.Y || mousePos.Y > bottomRight.Y;
        }

    }
}
