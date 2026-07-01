using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using RetroBar.Converters;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskButton.xaml
    /// </summary>
    public partial class TaskButton : UserControl
    {
        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(TaskList), typeof(TaskButton));

        public TaskList Host
        {
            get { return (TaskList)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        private ApplicationWindow Window;
        private TaskButtonStyleConverter StyleConverter = new TaskButtonStyleConverter();
        private ApplicationWindow.WindowState PressedWindowState = ApplicationWindow.WindowState.Inactive;

        private DelayedActivationHandler dragHandler;
        private bool _isLoaded;
        private bool _dragMouseDown;
        private Point _dragStartPoint;
        private LowLevelMouseHook _contextMenuHook;

        public TaskButton()
        {
            InitializeComponent();
            SetStyle();
        }

        private void SetStyle()
        {
            MultiBinding multiBinding = new MultiBinding();
            multiBinding.Converter = StyleConverter;

            multiBinding.Bindings.Add(new Binding { RelativeSource = RelativeSource.Self });
            multiBinding.Bindings.Add(new Binding("State"));

            AppButton.SetBinding(StyleProperty, multiBinding);
        }

        private void ScrollIntoView()
        {
            if (Window == null)
            {
                return;
            }

            if (Window.State == ApplicationWindow.WindowState.Active)
            {
                BringIntoView();
            }
        }

        private void Animate()
        {
            var ease = new SineEase();
            ease.EasingMode = EasingMode.EaseInOut;

            DoubleAnimation animation = new DoubleAnimation();
            animation.From = 0;
            animation.To = Host?.ButtonWidth ?? ActualWidth;
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(250));
            animation.FillBehavior = FillBehavior.Stop;
            animation.EasingFunction = ease;
            Storyboard.SetTarget(animation, this);
            Storyboard.SetTargetProperty(animation, new PropertyPath(WidthProperty));

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void TaskButton_OnLoaded(object sender, RoutedEventArgs e)
        {
            Window = DataContext as ApplicationWindow;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            dragHandler = new DelayedActivationHandler(() =>
            {
                Window?.BringToFront();
            });

            if (Window != null)
            {
                Window.GetButtonRect += Window_GetButtonRect;
                Window.PropertyChanged += Window_PropertyChanged;
            }

            if (Settings.Instance.SlideTaskbarButtons && Host?.Host?.Orientation == Orientation.Horizontal)
            {
                Animate();
            }

            Host?.RefreshGroupVisual(this);
            _isLoaded = true;
        }

        public void SetGroupColor(Color? color)
        {
            GroupIndicatorRect.BorderBrush = color.HasValue
                ? new SolidColorBrush(color.Value)
                : Brushes.Transparent;
            var vis = color.HasValue ? Visibility.Visible : Visibility.Collapsed;
            RemoveFromGroupMenuItem.Visibility = vis;
            RemoveGroupMenuItem.Visibility = vis;
            GroupNewColorMenuItem.Visibility = vis;
            CollapseGroupMenuItem.Visibility = vis;
            CollapseAllMenuItem.Visibility = vis;
            UncollapseAllMenuItem.Visibility = vis;
        }

        private void Window_GetButtonRect(ref NativeMethods.ShortRect rect)
        {
            if (Host?.Host?.Screen.Primary != true && Settings.Instance.MultiMonMode != MultiMonOption.SameAsWindow)
            {
                // If there are multiple instances of a button, use the button on the primary display only
                return;
            }

            Point buttonTopLeft = PointToScreen(new Point(0, 0));
            Point buttonBottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            rect.Top = (short)buttonTopLeft.Y;
            rect.Left = (short)buttonTopLeft.X;
            rect.Bottom = (short)buttonBottomRight.Y;
            rect.Right = (short)buttonBottomRight.X;
        }

        private void Window_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "State")
            {
                ScrollIntoView();
            }
        }

        private void TaskButton_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            dragHandler?.Dispose();

            if (Window != null)
            {
                Window.GetButtonRect -= Window_GetButtonRect;
                Window.PropertyChanged -= Window_PropertyChanged;
            }

            _isLoaded = false;
        }

        private void AppButton_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            ShellLogger.Debug($"TaskButton: ContextMenuOpening for {Window?.Title}");
            ShellFlyoutHelper.DismissIfActive();
            ShellLogger.Debug($"TaskButton: DismissIfActive returned, proceeding with context menu");

            if (Window == null)
            {
                return;
            }

            NativeMethods.WindowShowStyle wss = Window.ShowStyle;
            int ws = Window.WindowStyles;

            // disable window operations depending on current window state. originally tried implementing via bindings but found there is no notification we get regarding maximized state
            MaximizeMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowMaximized && (ws & (int)NativeMethods.WindowStyles.WS_MAXIMIZEBOX) != 0;
            MinimizeMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowMinimized && Window.CanMinimize;
            if (RestoreMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowNormal)
            {
                CloseMenuItem.FontWeight = FontWeights.Normal;
                RestoreMenuItem.FontWeight = FontWeights.Bold;
            }
            if (!RestoreMenuItem.IsEnabled || RestoreMenuItem.IsEnabled && !MaximizeMenuItem.IsEnabled)
            {
                CloseMenuItem.FontWeight = FontWeights.Bold;
                RestoreMenuItem.FontWeight = FontWeights.Normal;
            }
            MoveMenuItem.IsEnabled = wss == NativeMethods.WindowShowStyle.ShowNormal;
            SizeMenuItem.IsEnabled = wss == NativeMethods.WindowShowStyle.ShowNormal && (ws & (int)NativeMethods.WindowStyles.WS_MAXIMIZEBOX) != 0;

            SendToWorkspaceMenuItem.Items.Clear();
            var groupWindows = Host?.GetGroupWindows(Window) ?? new System.Collections.Generic.List<ApplicationWindow> { Window };
            for (int i = 1; i <= Utilities.WorkspaceManager.WorkspaceCount; i++)
            {
                int wsNum = i;
                int wsCount = Utilities.WorkspaceManager.Instance.GetWorkspaceWindowCount(i);
                bool isWindowHere = Utilities.WorkspaceManager.Instance.GetWindowWorkspace(Window.Handle) == i;
                var wsItem = new MenuItem
                {
                    Header = $"Workspace {i} ({wsCount})",
                    IsCheckable = true,
                    IsChecked = isWindowHere,
                    IsEnabled = !isWindowHere
                };
                wsItem.Click += (_, _) =>
                {
                    foreach (var w in groupWindows)
                        Utilities.WorkspaceManager.Instance.MoveWindowToWorkspace(w.Handle, wsNum);
                };
                SendToWorkspaceMenuItem.Items.Add(wsItem);
            }

            int exStyle = NativeMethods.GetWindowLong(Window.Handle, NativeMethods.GWL_EXSTYLE);
            AlwaysOnTopMenuItem.IsChecked = (exStyle & (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOPMOST) != 0;
            CenterOnScreenMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowMinimized;
            OpenContainingFolderMenuItem.Visibility = (!Window.IsUWP && !string.IsNullOrEmpty(Window.WinFileName))
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CloseMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Close();
        }

        private void EndTaskMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (Window != null)
            {
                ForceEndTask();
            }
        }

        private void ForceEndTask()
        {
            try
            {
                if (Window.ProcId.HasValue && Window.ProcId.Value != 0)
                {
                    // Don't kill RetroBar itself - just close the window gracefully
                    int currentProcId = Process.GetCurrentProcess().Id;
                    if (Window.ProcId.Value == currentProcId)
                    {
                        Window?.Close();
                        return;
                    }

                    Process process = Process.GetProcessById((int)Window.ProcId.Value);
                    process.Kill();
                }
            }
            catch (Exception)
            {
                Window?.Close();
            }
        }

        private void RestoreMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Restore();
        }

        private void MoveMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Move();
        }

        private void SizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Size();
        }

        private void MinimizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Minimize();
        }

        private void MaximizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Maximize();
        }

        private void RemoveFromGroupMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Host?.UngroupWindow(Window);
        }

        private void RemoveGroupMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Host?.RemoveGroup(Window);
        }

        private void GroupNewColorMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Host?.ChangeGroupColor(Window);
        }

        private void CollapseGroupMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Host?.CollapseGroup(Window);
        }

        private void CollapseAllMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Host?.CollapseAllGroups();
        }

        private void UncollapseAllMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Host?.UncollapseAllGroups();
        }

        private void AlwaysOnTopMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (Window == null) return;
            // IsChecked reflects the new desired state after WPF toggles it on click.
            IntPtr insertAfter = AlwaysOnTopMenuItem.IsChecked
                ? new IntPtr((int)NativeMethods.WindowZOrder.HWND_TOPMOST)
                : new IntPtr((int)NativeMethods.WindowZOrder.HWND_NOTOPMOST);
            NativeMethods.SetWindowPos(Window.Handle, insertAfter, 0, 0, 0, 0,
                (int)(NativeMethods.SetWindowPosFlags.SWP_NOMOVE | NativeMethods.SetWindowPosFlags.SWP_NOSIZE | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE));
        }

        private void CenterOnScreenMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (Window == null) return;
            NativeMethods.GetWindowRect(Window.Handle, out NativeMethods.Rect winRect);
            int winW = winRect.Right - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            var screen = System.Windows.Forms.Screen.FromHandle(Window.Handle);
            var wa = screen.WorkingArea;
            int x = wa.Left + (wa.Width - winW) / 2;
            int y = wa.Top + (wa.Height - winH) / 2;
            NativeMethods.SetWindowPos(Window.Handle, IntPtr.Zero, x, y, 0, 0,
                (int)(NativeMethods.SetWindowPosFlags.SWP_NOSIZE | NativeMethods.SetWindowPosFlags.SWP_NOZORDER));
        }

        private void OpenContainingFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (Window == null || string.IsNullOrEmpty(Window.WinFileName)) return;
            try
            {
                string dir = Path.GetDirectoryName(Window.WinFileName);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start("explorer.exe", $"/select,\"{Window.WinFileName}\"");
            }
            catch { }
        }

        private void AppButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PressedWindowState == ApplicationWindow.WindowState.Active && Window?.CanMinimize == true)
            {
                Window?.Minimize();
            }
            else
            {
                Window?.BringToFront();
            }
        }

        private void AppButton_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Right-click: DismissIfActive is called in AppButton_OnContextMenuOpening instead,
            // after mouse-up, to avoid blocking here while a queued right mouse-up closes the menu.
            if (e.ChangedButton != MouseButton.Right)
            {
                ShellFlyoutHelper.DismissIfActive();
            }
            if (e.ChangedButton == MouseButton.Left)
            {
                PressedWindowState = Window.State;
                _dragMouseDown = true;
                _dragStartPoint = e.GetPosition(this);
            }
        }

        private void AppButton_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (Host == null)
            {
                return;
            }

            if (Host.IsDraggingButton)
            {
                Host.UpdateButtonDrag(e);
                return;
            }

            if (!_dragMouseDown || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Vector moved = e.GetPosition(this) - _dragStartPoint;
            if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                Host.StartButtonDrag(this, e);
            }
        }

        private void AppButton_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            _dragMouseDown = false;

            if (Host?.IsDraggingButton == true)
            {
                // Suppress the click that would otherwise activate the window after a drag. End the
                // drag directly (and release capture); EndButtonDrag is idempotent so the resulting
                // LostMouseCapture is a harmless no-op.
                e.Handled = true;
                Host.EndButtonDrag();
                AppButton.ReleaseMouseCapture();
            }
        }

        private void AppButton_OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            _dragMouseDown = false;

            if (Host?.IsDraggingButton == true)
            {
                Host.EndButtonDrag();
            }
        }

        private void AppButton_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (Window == null || Settings.Instance.TaskMiddleClickAction == TaskMiddleClickOption.DoNothing)
                {
                    return;
                }
                if (Settings.Instance.TaskMiddleClickAction == TaskMiddleClickOption.CloseTask !=
                    (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
                {
                    Window?.Close();
                }
                else
                {
                    ShellHelper.StartProcess(Window.IsUWP ? "appx:" + Window.AppUserModelID : Window.WinFileName);
                }
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.Theme))
            {
                SetStyle();
            }
        }

        private void AppButton_OnDragEnter(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragEnter(e);
        }

        private void AppButton_OnDragLeave(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragLeave();
        }

        private void ContextMenu_OpenedOrClosed(object sender, RoutedEventArgs e)
        {
            string eventName = e.RoutedEvent == ContextMenu.OpenedEvent ? "Opened" : "Closed";
            ShellLogger.Debug($"TaskButton: ContextMenu_{eventName} for {Window?.Title}, flyoutActive={ShellFlyoutHelper.IsShellFlyoutActive()}, stack={new StackTrace(true)}");

            BindingOperations.GetMultiBindingExpression(AppButton, StyleProperty).UpdateTarget();

            if (e.RoutedEvent == ContextMenu.OpenedEvent)
            {
                _contextMenuHook = new LowLevelMouseHook();
                _contextMenuHook.LowLevelMouseEvent += OnContextMenuMouseEvent;
                _contextMenuHook.Initialize();

                if (Host?.Host?.hotkeyManager is { } hm)
                    hm.EscapeKeyDown += CloseContextMenuOnEscape;
            }
            else
            {
                _contextMenuHook?.Dispose();
                _contextMenuHook = null;

                if (Host?.Host?.hotkeyManager is { } hm)
                    hm.EscapeKeyDown -= CloseContextMenuOnEscape;
            }
        }

        private void CloseContextMenuOnEscape()
        {
            var menu = AppButton?.ContextMenu;
            Dispatcher.BeginInvoke(() =>
            {
                if (menu?.IsOpen == true)
                    menu.IsOpen = false;
            });
        }

        private static bool IsPointInsideMenuCascade(LowLevelMouseHook.POINT pt)
        {
            foreach (PresentationSource source in PresentationSource.CurrentSources)
            {
                if (source is HwndSource hwndSource
                    && source.RootVisual?.GetType().Name == "PopupRoot")
                {
                    NativeMethods.GetWindowRect(hwndSource.Handle, out NativeMethods.Rect r);
                    if (pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom)
                        return true;
                }
            }
            return false;
        }

        private void OnContextMenuMouseEvent(object sender, LowLevelMouseHook.LowLevelMouseEventArgs args)
        {
            if (args.Message != NativeMethods.WM.LBUTTONDOWN && args.Message != NativeMethods.WM.RBUTTONDOWN)
                return;

            var menu = AppButton?.ContextMenu;
            if (menu == null || !menu.IsOpen) return;

            try
            {
                var pt = args.HookStruct.pt;
                bool inside = IsPointInsideMenuCascade(pt);
                if (!inside)
                {
                    Dispatcher.BeginInvoke(() => { if (menu.IsOpen) menu.IsOpen = false; });
                }
            }
            catch { }
        }
    }
}
