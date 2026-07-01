using ManagedShell.Common.Logging;
using System.Diagnostics;
using System.Text;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    /// <summary>
    /// One-shot diagnostic snapshot of every top-level Shell_TrayWnd window currently alive,
    /// used to investigate the explorer.exe kill/relaunch race during boot (see
    /// ExplorerHotkeyStealer, EarlyWinBReservation, App.xaml.cs SetupManagedShell). EnumWindows is
    /// used instead of WindowHelper.FindWindowsTray so that ALL Shell_TrayWnd instances are found,
    /// not just the ones FindWindow's "first" + "next ignoring hwndIgnore" pairing happens to walk.
    /// </summary>
    internal static class ShellTrayWindowDiagnostics
    {
        public static void LogShellTrayWindows(string context)
        {
            int count = 0;

            EnumWindows((hwnd, _) =>
            {
                StringBuilder className = new StringBuilder(256);
                GetClassName(hwnd, className, className.Capacity);

                if (className.ToString() == "Shell_TrayWnd")
                {
                    count++;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    string procName = "?";
                    try { procName = Process.GetProcessById((int)pid).ProcessName; }
                    catch { /* process may have exited between EnumWindows and here */ }

                    ShellLogger.Info($"{context}: Found Shell_TrayWnd HWND=0x{hwnd:X} PID={pid} ({procName})");
                }

                return true;
            }, 0);

            if (count == 0)
                ShellLogger.Info($"{context}: No Shell_TrayWnd windows found");
        }
    }
}
