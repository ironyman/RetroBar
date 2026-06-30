using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    public class WorkspaceManager
    {
        public static readonly WorkspaceManager Instance = new WorkspaceManager();

        private int _currentWorkspace = 1;
        private readonly Dictionary<IntPtr, int> _windowWorkspaces = new();
        private readonly HashSet<IntPtr> _hiddenByUs = new();
        private ObservableCollection<ApplicationWindow> _windows;

        public const int WorkspaceCount = 9;
        public int CurrentWorkspace => _currentWorkspace;

        public event EventHandler WorkspaceSwitched;

        private WorkspaceManager() { }

        public void Initialize(ObservableCollection<ApplicationWindow> windows)
        {
            _windows = windows;

            foreach (ApplicationWindow w in _windows)
            {
                if (!_windowWorkspaces.ContainsKey(w.Handle))
                    _windowWorkspaces[w.Handle] = _currentWorkspace;
            }

            _windows.CollectionChanged += Windows_CollectionChanged;
        }

        private void Windows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (ApplicationWindow w in e.NewItems)
                {
                    if (!_windowWorkspaces.ContainsKey(w.Handle))
                        _windowWorkspaces[w.Handle] = _currentWorkspace;
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (ApplicationWindow w in e.OldItems)
                {
                    // Hiding a window makes the OS fire HSHELL_WINDOWDESTROYED, so the shell
                    // removes it from the collection exactly as if it had closed. If we're the
                    // ones who hid it, the window still exists — keep its workspace assignment
                    // so we can restore it when switching back. Only forget genuine closes.
                    if (_hiddenByUs.Contains(w.Handle))
                        continue;

                    _windowWorkspaces.Remove(w.Handle);
                }
            }
        }

        private static void SetWindowVisible(IntPtr hwnd, bool visible)
        {
            ShowWindow(hwnd, visible ? WindowShowStyle.ShowNoActivate : WindowShowStyle.Hide);
        }

        public int GetWindowWorkspace(IntPtr hwnd)
        {
            return _windowWorkspaces.TryGetValue(hwnd, out int ws) ? ws : 1;
        }

        public bool IsHiddenByUs(IntPtr hwnd) => _hiddenByUs.Contains(hwnd);

        public void MoveWindowToWorkspace(IntPtr hwnd, int workspace)
        {
            if (workspace < 1 || workspace > WorkspaceCount) return;

            _windowWorkspaces[hwnd] = workspace;

            if (workspace != _currentWorkspace)
            {
                if (_hiddenByUs.Add(hwnd))
                    SetWindowVisible(hwnd, false);
            }
            else
            {
                if (_hiddenByUs.Remove(hwnd))
                    SetWindowVisible(hwnd, true);
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public void SwitchToWorkspace(int workspace)
        {
            if (workspace < 1 || workspace > WorkspaceCount) return;
            if (workspace == _currentWorkspace || _windows == null) return;

            ShellLogger.Info($"WorkspaceManager: Switching to workspace {workspace}");
            _currentWorkspace = workspace;

            // Hide currently-visible windows that don't belong to the new workspace.
            foreach (var window in _windows.ToList())
            {
                if (GetWindowWorkspace(window.Handle) != workspace)
                {
                    if (_hiddenByUs.Add(window.Handle))
                        SetWindowVisible(window.Handle, false);
                }
            }

            // Show windows that belong to the new workspace. Hidden windows have already
            // left _windows (the shell treats a hidden window as destroyed), so restore
            // them from our own tracking rather than from the collection.
            foreach (var hwnd in _hiddenByUs.ToList())
            {
                if (GetWindowWorkspace(hwnd) != workspace)
                    continue;

                _hiddenByUs.Remove(hwnd);

                if (IsWindow(hwnd))
                    SetWindowVisible(hwnd, true);
                else
                    _windowWorkspaces.Remove(hwnd); // closed while hidden — forget it
            }

            WorkspaceSwitched?.Invoke(this, EventArgs.Empty);
        }

        public int GetWorkspaceWindowCount(int workspace)
        {
            // Count from the persistent map, not _windows: windows on other workspaces
            // are hidden and have been dropped from the shell's collection.
            return _windowWorkspaces.Count(kvp => kvp.Value == workspace && IsWindow(kvp.Key));
        }
    }
}
