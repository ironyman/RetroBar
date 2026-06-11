using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    public class HotkeyManager : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private readonly HotkeyListenerWindow _listenerWindow;
        private LowLevelKeyboardHook _keyboardHook;
        private const int TOGGLE_DESKTOP = 407;

        public HotkeyManager()
        {
            _listenerWindow = new HotkeyListenerWindow(this);

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            // Defer hotkey registration and keyboard hook installation until the application is
            // idle after startup. Two problems are avoided by waiting:
            //
            // 1. Hook timeout: WH_KEYBOARD_LL callbacks are dispatched on the thread that called
            //    SetWindowsHookEx. If that thread is busy (e.g. loading themes or creating windows),
            //    Windows skips the callback after ~300ms and the first Win+B press slips through to
            //    sihost/ShellExperienceHost, which focuses Explorer's tray instead of RetroBar.
            //
            // 2. sihost re-registration race: sihost detects that Win+B was unregistered and
            //    re-registers it after a short delay. By waiting until idle we register after that
            //    re-registration has settled, so our RegisterHotKey call wins.
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(InitializeHotkeys));
        }

        private void InitializeHotkeys()
        {
            ShellLogger.Info("HotkeyManager: Initializing hotkeys (deferred to application idle)");

            // Register Win+B and Win+D via RegisterHotKey first (same mechanism as Win+1-9,
            // which suppresses the Start menu automatically when Win is used as a modifier).
            _listenerWindow.RegisterSystemHotkeys();

            // Keyboard hook covers Win+B/D only if RegisterHotKey failed for them.
            _keyboardHook = new LowLevelKeyboardHook();
            _keyboardHook.IgnoreBKey = _listenerWindow.IsBRegistered;
            _keyboardHook.IgnoreDKey = _listenerWindow.IsDRegistered;
            _keyboardHook.FocusTrayRequested += OnFocusTrayRequested;
            _keyboardHook.ShowDesktopRequested += OnShowDesktopRequested;
            _keyboardHook.Initialize();

            if (Settings.Instance.WinNumHotkeysAction != WinNumHotkeysOption.WindowsDefault)
                _listenerWindow.RegisterNumberHotkeys();

            _listenerWindow.RegisterVirtualDesktopHotkeys();
        }

        // State for Win+D foreground tracking across two consecutive presses
        private IntPtr _savedForeground = IntPtr.Zero;
        private bool _desktopShowing = false;

        private void OnFocusTrayRequested() =>
            FocusTrayHotkeyPressed?.Invoke(this, EventArgs.Empty);

        private void OnShowDesktopRequested() => DoToggleDesktop();

        internal void DoToggleDesktop()
        {
            IntPtr tray = WindowHelper.FindWindowsTray(IntPtr.Zero);
            if (!_desktopShowing)
            {
                _savedForeground = GetForegroundWindow();
                _desktopShowing = true;
                SendMessage(tray, (int)WM.COMMAND, (IntPtr)TOGGLE_DESKTOP, IntPtr.Zero);
            }
            else
            {
                _desktopShowing = false;
                if (_savedForeground != IntPtr.Zero)
                {
                    IntPtr hwndToRestore = _savedForeground;
                    _savedForeground = IntPtr.Zero;
                    // Grant foreground activation rights to all processes now, while we still hold
                    // the WM_HOTKEY foreground-activation permission. The timer fires after the
                    // permission would have expired, but AllowSetForegroundWindow pre-authorizes it.
                    AllowSetForegroundWindow(0xFFFFFFFF);
                    SendMessage(tray, (int)WM.COMMAND, (IntPtr)TOGGLE_DESKTOP, IntPtr.Zero);
                    // Three-tier focus-restore strategy:
                    //
                    // Tier 1 — AllowSetForegroundWindow + timer (called above, before SendMessage):
                    //   Pre-grants foreground-activation rights to all processes while we still hold
                    //   the WM_HOTKEY permission token. We then delay ~150 ms so that Explorer's
                    //   TOGGLE_DESKTOP window-restoration (which is posted internally and completes
                    //   after SendMessage returns) has time to finish before we try to activate.
                    //
                    // Tier 2 — SwitchToThisWindow:
                    //   More forceful than SetForegroundWindow; bypasses some of the foreground-lock
                    //   rules. Used as a fallback if SetForegroundWindow returns false.
                    //
                    // Tier 3 — AttachThreadInput:
                    //   Temporarily joins our dispatcher thread's input queue to the target window's
                    //   thread so that our SetForegroundWindow call is treated as coming from a thread
                    //   that already owns the foreground, which the OS unconditionally allows.
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        ShowWindow(hwndToRestore, WindowShowStyle.Restore);

                        // Tier 1: standard activation, works when AllowSetForegroundWindow succeeded.
                        if (SetForegroundWindow(hwndToRestore))
                            return;

                        // Tier 2: SwitchToThisWindow is more aggressive and ignores some foreground
                        // lock rules; it also raises a minimized window as a side effect.
                        SwitchToThisWindow(hwndToRestore, true);
                        if (GetForegroundWindow() == hwndToRestore)
                            return;

                        // Tier 3: attach our input queue to the target's thread so the OS treats
                        // the subsequent SetForegroundWindow as coming from the foreground thread.
                        uint targetTid = GetWindowThreadProcessId(hwndToRestore, out _);
                        uint ourTid = GetCurrentThreadId();
                        if (targetTid != 0 && targetTid != ourTid)
                        {
                            AttachThreadInput(ourTid, targetTid, true);
                            SetForegroundWindow(hwndToRestore);
                            AttachThreadInput(ourTid, targetTid, false);
                        }
                    };
                    timer.Start();
                }
                else
                {
                    SendMessage(tray, (int)WM.COMMAND, (IntPtr)TOGGLE_DESKTOP, IntPtr.Zero);
                }
            }
        }

        public void Dispose()
        {
            if (_keyboardHook != null)
            {
                _keyboardHook.FocusTrayRequested -= OnFocusTrayRequested;
                _keyboardHook.ShowDesktopRequested -= OnShowDesktopRequested;
                _keyboardHook.Dispose();
            }
            _listenerWindow.UnregisterSystemHotkeys();
            _listenerWindow.UnregisterNumberHotkeys();
            _listenerWindow.UnregisterVirtualDesktopHotkeys();
            _listenerWindow?.Dispose();
        }

        #region Events
        public class TaskbarHotkeyEventArgs : EventArgs
        {
            public int index;
            public bool isShiftPressed;
        }

        public event EventHandler<TaskbarHotkeyEventArgs> TaskbarHotkeyPressed;
        public event EventHandler FocusTrayHotkeyPressed;

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.WinNumHotkeysAction))
            {
                if (!_listenerWindow.IsNumberHotkeysRegistered && Settings.Instance.WinNumHotkeysAction != WinNumHotkeysOption.WindowsDefault)
                    _listenerWindow.RegisterNumberHotkeys();
                else if (_listenerWindow.IsNumberHotkeysRegistered && Settings.Instance.WinNumHotkeysAction == WinNumHotkeysOption.WindowsDefault)
                    _listenerWindow.UnregisterNumberHotkeys();
            }
        }
        #endregion

        private class HotkeyListenerWindow : NativeWindow, IDisposable
        {
            internal bool IsNumberHotkeysRegistered => _registeredNumberHotkeys.Count > 0;
            internal bool IsBRegistered { get; private set; }
            internal bool IsDRegistered { get; private set; }

            private const int HOTKEY_ID_FOCUS_TRAY = 20;
            private const int HOTKEY_ID_SHOW_DESKTOP = 21;
            private const int HOTKEY_ID_ABSORBER = 22;
            private const int HOTKEY_ID_VDESK_SWITCH = 30; // +0..+8 for Win+F1..Win+F9
            private const int HOTKEY_ID_VDESK_MOVE = 40;   // +0..+8 for Win+Shift+F1..Win+Shift+F9
            private const int VDESK_HOTKEY_COUNT = 9;
            private const byte VK_F24 = 0x87;

            private readonly HotkeyManager _manager;
            private readonly HashSet<int> _registeredNumberHotkeys = [];
            private readonly HashSet<int> _registeredSystemHotkeys = [];
            private readonly HashSet<int> _registeredVirtualDesktopHotkeys = [];
            private const int WMTRAY_UNREGISTERHOTKEY = (int)WM.USER + 231;
            private List<TrayHotkey.Entry> _trayHotkeyTable;
            private bool _explorerResourcesLoaded;
            private IntPtr _explorerTrayWindow;   // Explorer's Shell_TrayWnd  (target for WMTRAY_UNREGISTERHOTKEY)
            private IntPtr _otherTrayWindow;      // ManagedShell's Shell_TrayWnd (also tried, just in case)

            public HotkeyListenerWindow(HotkeyManager manager)
            {
                CreateHandle(new CreateParams());
                _manager = manager;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == (int)WM.HOTKEY)
                {
                    int hotkeyId = m.WParam.ToInt32();

                    if (hotkeyId == HOTKEY_ID_FOCUS_TRAY)
                    {
                        _manager.FocusTrayHotkeyPressed?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    if (hotkeyId == HOTKEY_ID_SHOW_DESKTOP)
                    {
                        _manager.DoToggleDesktop();
                        return;
                    }

                    if (hotkeyId == HOTKEY_ID_ABSORBER)
                        return; // Win+F24 mask key — kernel dispatched WM_HOTKEY which suppresses Start menu; nothing else to do

                    if (hotkeyId >= HOTKEY_ID_VDESK_SWITCH && hotkeyId < HOTKEY_ID_VDESK_SWITCH + VDESK_HOTKEY_COUNT)
                    {
                        VirtualDesktopHelper.SwitchToDesktop(hotkeyId - HOTKEY_ID_VDESK_SWITCH);
                        return;
                    }

                    if (hotkeyId >= HOTKEY_ID_VDESK_MOVE && hotkeyId < HOTKEY_ID_VDESK_MOVE + VDESK_HOTKEY_COUNT)
                    {
                        IntPtr foreground = GetForegroundWindow();
                        if (foreground != IntPtr.Zero)
                            VirtualDesktopHelper.MoveWindowToDesktop(foreground, hotkeyId - HOTKEY_ID_VDESK_MOVE);
                        return;
                    }

                    if (_registeredNumberHotkeys.Contains(hotkeyId))
                    {
                        bool isShiftPressed = hotkeyId >= 10 && hotkeyId <= 19;
                        int taskIndex = isShiftPressed ? hotkeyId - 10 : hotkeyId;
                        _manager.TaskbarHotkeyPressed?.Invoke(this, new TaskbarHotkeyEventArgs
                        {
                            index = taskIndex,
                            isShiftPressed = isShiftPressed
                        });
                        ShellLogger.Debug($"HotkeyManager: Hotkey pressed: ID={hotkeyId}, TaskIndex={taskIndex}, Shift={isShiftPressed}");
                    }
                }
                base.WndProc(ref m);
            }

            public void RegisterSystemHotkeys()
            {
                ShellLogger.Info("HotkeyManager: Registering system hotkeys (Win+B, Win+D)");
                try
                {
                    EnsureExplorerResourcesLoaded();

                    // Win+B on Windows 11 is owned by ShellExperienceHost (not Explorer).
                    // Try to unregister it from there before calling RegisterHotKey.
                    TryUnregisterFromProcess("ShellExperienceHost", VK.KEY_B);
                    TryUnregisterFromProcess("sihost", VK.KEY_B);

                    IsBRegistered = RegisterWinKey(VK.KEY_B, HOTKEY_ID_FOCUS_TRAY);
                    IsDRegistered = RegisterWinKey(VK.KEY_D, HOTKEY_ID_SHOW_DESKTOP);
                    if (IsBRegistered) _registeredSystemHotkeys.Add(HOTKEY_ID_FOCUS_TRAY);
                    if (IsDRegistered) _registeredSystemHotkeys.Add(HOTKEY_ID_SHOW_DESKTOP);

                    // Register Win+F24 so the kernel dispatches WM_HOTKEY when the hook injects F24
                    // while Win is held, marking Win as "used as modifier" to suppress Start menu.
                    if (RegisterHotKey(Handle, HOTKEY_ID_ABSORBER, (uint)(MOD.WIN | MOD.NOREPEAT), VK_F24))
                        _registeredSystemHotkeys.Add(HOTKEY_ID_ABSORBER);
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Exception during RegisterSystemHotkeys - {ex.Message}");
                }
            }

            /// <summary>
            /// Finds all windows belonging to the named process, reads its binary for the hotkey table,
            /// and sends WMTRAY_UNREGISTERHOTKEY to each window for the matching entry.
            /// This mirrors what WMTRAY_UNREGISTERHOTKEY does for Explorer, but targeting other shell processes.
            /// </summary>
            private void TryUnregisterFromProcess(string processName, VK key)
            {
                try
                {
                    // Collect one representative window handle per process instance (pid → hwnd)
                    var processWindows = new Dictionary<uint, IntPtr>();
                    var collectCallback = new CallBackPtr((hwnd, _) =>
                    {
                        GetWindowThreadProcessId(hwnd, out uint pid);
                        if (!processWindows.ContainsKey(pid))
                        {
                            try
                            {
                                using var p = Process.GetProcessById((int)pid);
                                if (p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                                    processWindows[pid] = hwnd;
                            }
                            catch { }
                        }
                        return true;
                    });
                    EnumWindows(collectCallback, 0);

                    foreach (var kv in processWindows)
                    {
                        uint pid = kv.Key;
                        IntPtr anyHwnd = kv.Value;

                        List<TrayHotkey.Entry> table;
                        try { table = TrayHotkey.BuildTable(anyHwnd); }
                        catch { continue; }

                        if (table.Count == 0)
                        {
                            ShellLogger.Debug($"HotkeyManager: {processName} hotkey table is empty");
                            continue;
                        }

                        int idx = table.FindIndex(e => e.VirtualKey == (byte)key && (e.Modifier & (byte)MOD.WIN) != 0);
                        if (idx < 0)
                        {
                            ShellLogger.Debug($"HotkeyManager: {key} not found in {processName} binary hotkey table");
                            continue;
                        }

                        int hotkeyId = table[idx].Id;
                        ShellLogger.Debug($"HotkeyManager: {key} found in {processName} binary at table ID={hotkeyId}; sending WMTRAY_UNREGISTERHOTKEY to all its windows");

                        var sendCallback = new CallBackPtr((hwnd, _) =>
                        {
                            GetWindowThreadProcessId(hwnd, out uint windowPid);
                            if (windowPid == pid)
                                SendMessage(hwnd, WMTRAY_UNREGISTERHOTKEY, new IntPtr(hotkeyId), IntPtr.Zero);
                            return true;
                        });
                        EnumWindows(sendCallback, 0);
                    }
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Exception unregistering {key} from {processName} - {ex.Message}");
                }
            }

            public void RegisterNumberHotkeys()
            {
                ShellLogger.Info("HotkeyManager: Registering number hotkeys");
                try
                {
                    EnsureExplorerResourcesLoaded();

                    if (RegisterWinKey(VK.KEY_1, 0)) _registeredNumberHotkeys.Add(0);
                    if (RegisterWinKey(VK.KEY_2, 1)) _registeredNumberHotkeys.Add(1);
                    if (RegisterWinKey(VK.KEY_3, 2)) _registeredNumberHotkeys.Add(2);
                    if (RegisterWinKey(VK.KEY_4, 3)) _registeredNumberHotkeys.Add(3);
                    if (RegisterWinKey(VK.KEY_5, 4)) _registeredNumberHotkeys.Add(4);
                    if (RegisterWinKey(VK.KEY_6, 5)) _registeredNumberHotkeys.Add(5);
                    if (RegisterWinKey(VK.KEY_7, 6)) _registeredNumberHotkeys.Add(6);
                    if (RegisterWinKey(VK.KEY_8, 7)) _registeredNumberHotkeys.Add(7);
                    if (RegisterWinKey(VK.KEY_9, 8)) _registeredNumberHotkeys.Add(8);
                    if (RegisterWinKey(VK.KEY_0, 9)) _registeredNumberHotkeys.Add(9);

                    if (RegisterWinKey(VK.KEY_1, 10, MOD.SHIFT)) _registeredNumberHotkeys.Add(10);
                    if (RegisterWinKey(VK.KEY_2, 11, MOD.SHIFT)) _registeredNumberHotkeys.Add(11);
                    if (RegisterWinKey(VK.KEY_3, 12, MOD.SHIFT)) _registeredNumberHotkeys.Add(12);
                    if (RegisterWinKey(VK.KEY_4, 13, MOD.SHIFT)) _registeredNumberHotkeys.Add(13);
                    if (RegisterWinKey(VK.KEY_5, 14, MOD.SHIFT)) _registeredNumberHotkeys.Add(14);
                    if (RegisterWinKey(VK.KEY_6, 15, MOD.SHIFT)) _registeredNumberHotkeys.Add(15);
                    if (RegisterWinKey(VK.KEY_7, 16, MOD.SHIFT)) _registeredNumberHotkeys.Add(16);
                    if (RegisterWinKey(VK.KEY_8, 17, MOD.SHIFT)) _registeredNumberHotkeys.Add(17);
                    if (RegisterWinKey(VK.KEY_9, 18, MOD.SHIFT)) _registeredNumberHotkeys.Add(18);
                    if (RegisterWinKey(VK.KEY_0, 19, MOD.SHIFT)) _registeredNumberHotkeys.Add(19);
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Exception during RegisterNumberHotkeys - {ex.Message}");
                }
            }

            private void EnsureExplorerResourcesLoaded()
            {
                if (_explorerResourcesLoaded) return;

                _trayHotkeyTable = [];
                _explorerTrayWindow = IntPtr.Zero;
                _otherTrayWindow = IntPtr.Zero;

                TryFindTrayWindows();
                TryBuildHotkeyTable();

                _explorerResourcesLoaded = true;
            }

            private void TryBuildHotkeyTable()
            {
                if (_explorerTrayWindow == IntPtr.Zero) return;
                try
                {
                    _trayHotkeyTable = TrayHotkey.BuildTable(_explorerTrayWindow);
                    ShellLogger.Debug($"HotkeyManager: Found {_trayHotkeyTable.Count} entries in Explorer hotkey table");
                    foreach (var entry in _trayHotkeyTable)
                        ShellLogger.Debug($"HotkeyManager:   ID={entry.Id} VK=0x{entry.VirtualKey:X2} MOD=0x{entry.Modifier:X2}");
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Failed to build Explorer hotkey table - {ex.Message}");
                }
            }

            /// <summary>
            /// Finds both Shell_TrayWnd instances. Uses process name to reliably distinguish
            /// Explorer's real tray window from ManagedShell's fake one, so that
            /// WMTRAY_UNREGISTERHOTKEY is sent to the correct (Explorer) window.
            /// </summary>
            private void TryFindTrayWindows()
            {
                try
                {
                    IntPtr first = WindowHelper.FindWindowsTray(IntPtr.Zero);
                    IntPtr second = WindowHelper.FindWindowsTray(first);

                    if (IsExplorerWindow(first))
                    {
                        _explorerTrayWindow = first;
                        _otherTrayWindow = second;
                    }
                    else if (IsExplorerWindow(second))
                    {
                        _explorerTrayWindow = second;
                        _otherTrayWindow = first;
                    }
                    else
                    {
                        // Can't identify — try both; use first as primary for table scan
                        _explorerTrayWindow = first;
                        _otherTrayWindow = second;
                        ShellLogger.Warning("HotkeyManager: Could not identify Explorer tray window by process name; using first found");
                    }

                    ShellLogger.Debug($"HotkeyManager: Explorer tray HWND=0x{_explorerTrayWindow:X}, other HWND=0x{_otherTrayWindow:X}");
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Failed to find tray windows - {ex.Message}");
                }
            }

            private static bool IsExplorerWindow(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero) return false;
                try
                {
                    GetWindowThreadProcessId(hwnd, out uint processId);
                    using var proc = Process.GetProcessById((int)processId);
                    return proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            private bool RegisterWinKey(VK key, int id, MOD additionalModifiers = 0)
            {
                MOD modifiers = MOD.WIN | MOD.NOREPEAT | additionalModifiers;
                try
                {
                    TryUnregisterTrayHotkey(key, additionalModifiers);

                    bool success = RegisterHotKey(Handle, id, (uint)modifiers, (uint)key);
                    if (success)
                        ShellLogger.Debug($"HotkeyManager: Registered hotkey {modifiers}+{key} with ID={id}");
                    else
                        ShellLogger.Warning($"HotkeyManager: Failed to register hotkey {modifiers}+{key} with ID={id} (Explorer may still own it)");
                    return success;
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Exception registering hotkey {modifiers}+{key} - {ex.Message}");
                    return false;
                }
            }

            private void TryUnregisterTrayHotkey(VK key, MOD additionalModifiers = 0)
            {
                try
                {
                    MOD searchModifier = MOD.WIN | additionalModifiers;
                    int idx = _trayHotkeyTable.FindIndex(e => e.VirtualKey == (byte)key && e.Modifier == (byte)searchModifier);
                    if (idx < 0)
                    {
                        ShellLogger.Debug($"HotkeyManager: {key} not found in tray hotkey table (may still succeed with RegisterHotKey)");
                        return;
                    }

                    int trayHotkeyId = _trayHotkeyTable[idx].Id;
                    // Send to both windows; one is guaranteed to be Explorer's
                    if (_explorerTrayWindow != IntPtr.Zero)
                        SendMessage(_explorerTrayWindow, WMTRAY_UNREGISTERHOTKEY, new IntPtr(trayHotkeyId), IntPtr.Zero);
                    if (_otherTrayWindow != IntPtr.Zero)
                        SendMessage(_otherTrayWindow, WMTRAY_UNREGISTERHOTKEY, new IntPtr(trayHotkeyId), IntPtr.Zero);
                    ShellLogger.Debug($"HotkeyManager: Sent WMTRAY_UNREGISTERHOTKEY for {key} (ID={trayHotkeyId}) to both tray windows");
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Exception unregistering Explorer hotkey - {ex.Message}");
                }
            }

            public void UnregisterSystemHotkeys()
            {
                foreach (int id in _registeredSystemHotkeys)
                    UnregisterHotKey(Handle, id);
                _registeredSystemHotkeys.Clear();
                IsBRegistered = false;
                IsDRegistered = false;
            }

            public void UnregisterNumberHotkeys()
            {
                ShellLogger.Info("HotkeyManager: Unregistering number hotkeys");
                foreach (int id in _registeredNumberHotkeys)
                {
                    UnregisterHotKey(Handle, id);
                    ShellLogger.Debug($"HotkeyManager: Unregistered hotkey ID={id}");
                }
                _registeredNumberHotkeys.Clear();
            }

            public void RegisterVirtualDesktopHotkeys()
            {
                ShellLogger.Info("HotkeyManager: Registering virtual desktop hotkeys (Win+F1-F9, Win+Shift+F1-F9)");
                VK[] fKeys = { VK.F1, VK.F2, VK.F3, VK.F4, VK.F5, VK.F6, VK.F7, VK.F8, VK.F9 };
                try
                {
                    for (int i = 0; i < fKeys.Length; i++)
                    {
                        if (RegisterHotKey(Handle, HOTKEY_ID_VDESK_SWITCH + i, (uint)(MOD.WIN | MOD.NOREPEAT), (uint)fKeys[i]))
                            _registeredVirtualDesktopHotkeys.Add(HOTKEY_ID_VDESK_SWITCH + i);
                        else
                            ShellLogger.Warning($"HotkeyManager: Failed to register Win+F{i + 1}");

                        if (RegisterHotKey(Handle, HOTKEY_ID_VDESK_MOVE + i, (uint)(MOD.WIN | MOD.NOREPEAT | MOD.SHIFT), (uint)fKeys[i]))
                            _registeredVirtualDesktopHotkeys.Add(HOTKEY_ID_VDESK_MOVE + i);
                        else
                            ShellLogger.Warning($"HotkeyManager: Failed to register Win+Shift+F{i + 1}");
                    }
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"HotkeyManager: Exception during RegisterVirtualDesktopHotkeys - {ex.Message}");
                }
            }

            public void UnregisterVirtualDesktopHotkeys()
            {
                foreach (int id in _registeredVirtualDesktopHotkeys)
                    UnregisterHotKey(Handle, id);
                _registeredVirtualDesktopHotkeys.Clear();
            }

            public void Dispose() => DestroyHandle();
        }
    }
}
