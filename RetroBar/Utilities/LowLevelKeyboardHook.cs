using ManagedShell.Common.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

        public delegate IntPtr LowLevelKeyboardProcDelegate(int code, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        // F24 (0x87) is used as the Start-menu mask key instead of VK_CONTROL to avoid
        // triggering apps that react to Ctrl. F24 is registered with RegisterHotKey so the
        // kernel dispatches WM_HOTKEY for Win+F24, which marks Win as "used as a modifier".
        private const byte VK_F24 = 0x87;
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

        public event Action FocusTrayRequested;
        public event Action ShowDesktopRequested;

        /// <summary>
        /// When true, Win+B is handled via RegisterHotKey and the hook should not intercept it.
        /// </summary>
        public bool IgnoreBKey { get; set; }

        /// <summary>
        /// When true, Win+D is handled via RegisterHotKey and the hook should not intercept it.
        /// </summary>
        public bool IgnoreDKey { get; set; }

        private IntPtr _hook = IntPtr.Zero;
        private readonly LowLevelKeyboardProcDelegate _hookDelegate;
        private bool _blockNextBUp;
        private bool _blockNextDUp;
        private bool _winChordIntercepted;

        public LowLevelKeyboardHook()
        {
            _hookDelegate = KeyboardHookProc;
        }

        public bool Initialize()
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, GetModuleHandle(curModule.ModuleName), 0);
            if (_hook == IntPtr.Zero)
            {
                ShellLogger.Warning("LowLevelKeyboardHook: Failed to install hook");
                return false;
            }
            return true;
        }

        private bool IsWinKeyDown() =>
            (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

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
                    if (!isInjected && IsWinKeyDown())
                    {
                        if (vk == (uint)VK.KEY_B && !IgnoreBKey)
                        {
                            FocusTrayRequested?.Invoke();
                            _blockNextBUp = true;
                            _winChordIntercepted = true;
                            return (IntPtr)1;
                        }
                        if (vk == (uint)VK.KEY_D && !IgnoreDKey)
                        {
                            ShowDesktopRequested?.Invoke();
                            _blockNextDUp = true;
                            _winChordIntercepted = true;
                            return (IntPtr)1;
                        }
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (vk == (uint)VK.KEY_B && _blockNextBUp)
                    {
                        _blockNextBUp = false;
                        return (IntPtr)1;
                    }
                    if (vk == (uint)VK.KEY_D && _blockNextDUp)
                    {
                        _blockNextDUp = false;
                        return (IntPtr)1;
                    }

                    if ((vk == VK_LWIN || vk == VK_RWIN) && !isInjected && _winChordIntercepted)
                    {
                        _winChordIntercepted = false;
                        // AutoHotkey technique: inject mask key DOWN+UP synchronously — before
                        // CallNextHookEx — so the OS sees Win+F24 while Win is still in "pending
                        // release" state. The hook is called recursively for the injected event,
                        // completing before Win UP passes through. Because Win+F24 is registered via
                        // RegisterHotKey, the kernel dispatches WM_HOTKEY which marks Win as "used
                        // as modifier", suppressing Start menu on the Win UP. F24 is used instead of
                        // VK_CONTROL to avoid side-effects in apps that react to Win+Ctrl.
                        keybd_event(VK_F24, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_F24, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        // Fall through to CallNextHookEx — do NOT return 1 here.
                        // Win UP must be passed through so Win key state is released properly.
                    }
                }
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
