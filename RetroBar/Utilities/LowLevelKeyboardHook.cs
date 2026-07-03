using ManagedShell.Common.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    public class LowLevelKeyboardHook : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProcDelegate callback, IntPtr hInstance, uint threadId);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr idHook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private const uint WM_QUIT = 0x0012;

        public delegate IntPtr LowLevelKeyboardProcDelegate(int code, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_SHIFT = 0x10;
        // F23 (0x86) is used as the Start-menu mask key instead of VK_CONTROL to avoid
        // triggering apps that react to Ctrl. F24 is registered with RegisterHotKey so the
        // kernel dispatches WM_HOTKEY for Win+F24, which marks Win as "used as a modifier".
        // https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes
        // For some reason F24 doesn't work.
        private const byte VK_F23 = 0x86;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        public event Action SwitchToFirstWorkspaceRequested;
        public event Action EscapeKeyDown;

        private IntPtr _hook = IntPtr.Zero;
        private readonly LowLevelKeyboardProcDelegate _hookDelegate;
        private bool _blockNextF1Up;
        private bool _winChordIntercepted;

        private Thread _hookThread;
        private int _hookThreadId;
        private readonly ManualResetEventSlim _hookThreadReady = new(false);
        private bool _hookThreadInitResult;

        public LowLevelKeyboardHook()
        {
            _hookDelegate = KeyboardHookProc;
        }

        public bool Initialize()
        {
            // WH_KEYBOARD_LL callbacks are dispatched via the message queue of the thread that
            // called SetWindowsHookEx. If that thread is busy for more than Windows' hook timeout
            // (~300ms by default), the OS skips the callback entirely and the keystroke falls
            // through to whatever the shell has reserved it for - e.g. Win+F1 opening Windows
            // Help instead of switching workspaces. The WPF dispatcher thread does layout/render
            // work that can occasionally exceed that window, so the hook is installed and pumped
            // on its own dedicated thread that does nothing else, keeping it responsive regardless
            // of what the rest of the app is doing.
            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "RetroBar Low-Level Keyboard Hook"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Priority = ThreadPriority.Highest;
            _hookThread.Start();
            _hookThreadReady.Wait();
            return _hookThreadInitResult;
        }

        private void HookThreadProc()
        {
            _hookThreadId = GetCurrentThreadId();

            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, GetModuleHandle(curModule.ModuleName), 0);
            }

            _hookThreadInitResult = _hook != IntPtr.Zero;
            if (!_hookThreadInitResult)
                ShellLogger.Warning("LowLevelKeyboardHook: Failed to install hook");

            _hookThreadReady.Set();

            if (!_hookThreadInitResult)
                return;

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private bool IsWinKeyDown() =>
            (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        private bool IsShiftDown() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var kbStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = kbStruct.vkCode;
                int msg = (int)wParam;
                bool isInjected = (kbStruct.flags & LLKHF_INJECTED) != 0;

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (vk == (uint)VK.ESCAPE)
                    {
                        EscapeKeyDown?.Invoke();
                    }
                    if (!isInjected && IsWinKeyDown())
                    {
                        // Win+F1 is reserved by the OS to launch Windows Help ("Get Help"). That
                        // reservation sits below the RegisterHotKey/WM_HOTKEY layer, so the only
                        // reliable way to repurpose it (switch to workspace 1) is to swallow the
                        // keystroke here before the shell sees it. Win+Shift+F1 (move window to
                        // workspace 1) is not reserved and stays on RegisterHotKey.
                        if (vk == (uint)VK.F1 && !IsShiftDown())
                        {
                            SwitchToFirstWorkspaceRequested?.Invoke();
                            _blockNextF1Up = true;
                            _winChordIntercepted = true;
                            return (IntPtr)1;
                        }
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (vk == (uint)VK.F1 && _blockNextF1Up)
                    {
                        _blockNextF1Up = false;
                        return (IntPtr)1;
                    }

                    if ((vk == VK_LWIN || vk == VK_RWIN) && !isInjected && _winChordIntercepted)
                    {
                        _winChordIntercepted = false;
                        // Block the natural Win UP and inject a synthetic one.
                        // Windows only opens Start menu on natural (non-injected) Win UP events,
                        // so the synthetic replacement cleans up key state without triggering
                        // the Start menu. The Win+F24 RegisterHotKey approach (which marks Win as
                        // "used as modifier") is unreliable because Win+F24 may itself fail to
                        // register on systems where another process owns it.
                        // keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        // return (IntPtr)1;
                        keybd_event(VK_F23, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_F23, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                }
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookThread == null) return;

            // Unhooking must happen on the thread that installed the hook, so signal the message
            // loop to exit and let HookThreadProc do the UnhookWindowsHookEx itself.
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(1000);
            _hookThread = null;
            _hookThreadReady.Dispose();
        }
    }
}
