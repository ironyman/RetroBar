using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ManagedShell.Interop;

namespace RetroBar.Utilities
{
    // Detects whether a window's owning process is running elevated (UAC). PROCESS_QUERY_LIMITED_INFORMATION
    // and TOKEN_QUERY are both permitted across integrity levels - the same trick Task Manager and Process
    // Explorer use to show a process's elevation state without themselves running elevated.
    internal static class ElevationHelper
    {
        // Checks our own process, not some other window's - no cross-process P/Invoke needed for this case,
        // just the current thread's token. RetroBar must run at Medium integrity (see docs\missing-tray-icons.md
        // and docs\shell-crash-recovery-failure.md): running elevated silently breaks UIPI-gated communication
        // with the (Medium-integrity) shell, e.g. ExplorerMonitor never receiving explorer's "TaskbarCreated"
        // broadcast after an explorer.exe restart, so the AppBar/work-area reservation never gets reasserted.
        public static bool IsCurrentProcessElevated()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private const uint TokenElevation = 20; // TOKEN_INFORMATION_CLASS.TokenElevation

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public int TokenIsElevated;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, uint tokenInformationClass,
            IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        public static bool IsWindowElevated(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.ProcessAccessFlags.QueryLimitedInformation, false, (int)pid);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKENQUERY, out IntPtr hToken))
                    return false;

                try
                {
                    int size = Marshal.SizeOf<TOKEN_ELEVATION>();
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!GetTokenInformation(hToken, TokenElevation, buffer, (uint)size, out _))
                            return false;

                        return Marshal.PtrToStructure<TOKEN_ELEVATION>(buffer).TokenIsElevated != 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(hToken);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }
    }
}
