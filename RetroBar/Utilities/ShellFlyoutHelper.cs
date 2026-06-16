using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;

namespace RetroBar.Utilities
{
    internal static class ShellFlyoutHelper
    {
        internal static bool IsShellFlyoutWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                var className = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, className, className.Capacity);
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint procId);

                string procName = Process.GetProcessById((int)procId).ProcessName;

                ShellLogger.Debug($"Taskbar: IsShellFlyoutWindow {procName}, {className}");

                if (className.ToString().Equals("WindowsDashboard", StringComparison.OrdinalIgnoreCase) &&
                    procName.Equals("Widgets", StringComparison.OrdinalIgnoreCase))
                    return true;

                return procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                    || procName.Equals("Shellhost", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsShellFlyoutActive()
        {
            return IsShellFlyoutWindow(NativeMethods.GetForegroundWindow());
        }

        internal static void DismissIfActive()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (!IsShellFlyoutWindow(foreground)) return;

            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[2];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].mkhi.ki.wVk = (ushort)NativeMethods.VK.ESCAPE;
            inputs[0].mkhi.ki.dwFlags = 0;
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].mkhi.ki.wVk = (ushort)NativeMethods.VK.ESCAPE;
            inputs[1].mkhi.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
            NativeMethods.SendInput(2, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));

            // Wait until the flyout is no longer foreground before returning so callers
            // don't race SetForegroundWindow against a flyout that is still dismissing.
            const int timeoutMs = 500;
            const int pollMs = 10;
            for (int elapsed = 0; elapsed < timeoutMs; elapsed += pollMs)
            {
                System.Threading.Thread.Sleep(pollMs);
                if (!IsShellFlyoutWindow(NativeMethods.GetForegroundWindow()))
                    return;
            }
        }
    }
}
