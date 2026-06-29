using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RetroBar.Controls
{
    public partial class NotifyIconSeparator : UserControl
    {
        public static DependencyProperty NotifyIconListHostProperty = DependencyProperty.Register(
            nameof(NotifyIconListHost), typeof(NotifyIconList), typeof(NotifyIconSeparator));

        public NotifyIconList NotifyIconListHost
        {
            get { return (NotifyIconList)GetValue(NotifyIconListHostProperty); }
            set { SetValue(NotifyIconListHostProperty, value); }
        }

        private bool _dragMouseDown;
        private Point _dragStartPoint;

        public NotifyIconSeparator()
        {
            InitializeComponent();
        }

        private void Separator_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            _dragMouseDown = true;
            _dragStartPoint = e.GetPosition(SeparatorRoot);
        }

        private void Separator_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (NotifyIconListHost?.IsDraggingIcon == true)
            {
                NotifyIconListHost.UpdateIconDrag(e);
                return;
            }

            if (!_dragMouseDown || e.LeftButton != MouseButtonState.Pressed)
                return;

            Vector moved = e.GetPosition(SeparatorRoot) - _dragStartPoint;
            if (System.Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                System.Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                NotifyIconListHost?.StartIconDrag(this, e);
                if (NotifyIconListHost?.IsDraggingIcon == true)
                    SeparatorRoot.CaptureMouse();
            }
        }

        private void Separator_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            _dragMouseDown = false;

            if (NotifyIconListHost?.IsDraggingIcon == true)
            {
                NotifyIconListHost.EndIconDrag();
                SeparatorRoot.ReleaseMouseCapture();
            }
        }

        private void Separator_OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            _dragMouseDown = false;
            if (NotifyIconListHost?.IsDraggingIcon == true)
                NotifyIconListHost.EndIconDrag();
        }
    }
}
