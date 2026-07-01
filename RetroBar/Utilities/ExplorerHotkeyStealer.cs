using ManagedShell.Common.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Steals Win+B from explorer.exe by killing it, registering the hotkey while no process is
    /// alive to hold it, then relaunching explorer.exe. Unlike sihost.exe, explorer.exe does not
    /// auto-restart when killed (TerminateProcess doesn't trigger its crash-recovery registration),
    /// so this is deterministic: once it has fully exited, nothing can be holding the hotkey, and we
    /// control exactly when it comes back. We have to do this for Win+B because explorer
    /// doesn't release Win+B unlike the other hotkeys. We reserve at early init because if it restart
    /// explorer after RetroBay tray Shell_TrayWnd registration, then explorer will launch with a new
    /// browser instead of just launching as shell. When explorer starts without an existing Shell_Traywnd
    /// then it launches as shell without explorer shell.
    ///
    /// Process.Start returns as soon as the new explorer.exe process exists, long before it finishes
    /// deciding whether to become the shell (which it signals by calling SetShellWindow). On a busy
    /// boot, that decision can take long enough that our own ShellManager creates its fake Shell_TrayWnd
    /// first, causing the new explorer.exe to see an existing tray and open a File Explorer browser
    /// window instead - and causing App.SetupManagedShell's GetShellWindow() check to run before
    /// explorer registers, wrongly concluding RetroBar itself is the shell. We block here until
    /// GetShellWindow() is set (or we time out) so callers never proceed past this point before
    /// explorer has won that race.
    /// </summary>
    internal static class ExplorerHotkeyStealer
    {
        private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        private static readonly TimeSpan ShellWaitTimeout = TimeSpan.FromSeconds(10);
        private const int ShellWaitPollIntervalMs = 50;

        /// <summary>
        /// Kills explorer.exe, invokes <paramref name="registerHotkey"/> while it's dead, then
        /// relaunches it. Launching explorer.exe with no arguments while no shell is running starts
        /// the desktop/taskbar shell directly; it does not open a File Explorer window.
        /// </summary>
        public static bool StealWinBFromExplorer(Action registerHotkey)
        {
            try
            {
                var existing = Process.GetProcessesByName("explorer");
                if (existing.Length == 0)
                {
                    ShellLogger.Warning("ExplorerHotkeyStealer: explorer.exe not running, nothing to steal Win+B from");
                    return false;
                }

                ShellLogger.Info($"ExplorerHotkeyStealer: GetShellWindow()=0x{GetShellWindow():X} before kill; PIDs to kill: {string.Join(",", Array.ConvertAll(existing, p => p.Id))}");

                foreach (var p in existing)
                {
                    try { p.Kill(); p.WaitForExit(5000); }
                    catch (Exception ex) { ShellLogger.Warning($"ExplorerHotkeyStealer: Failed to kill explorer.exe (PID={p.Id}) - {ex.Message}"); }
                    finally { p.Dispose(); }
                }

                ShellLogger.Info("ExplorerHotkeyStealer: Killed explorer.exe, registering Win+B while it's dead");
                ShellTrayWindowDiagnostics.LogShellTrayWindows("ExplorerHotkeyStealer: after kill, before registerHotkey");
                registerHotkey();

                var relaunched = Process.Start(ExplorerPath);
                ShellLogger.Info($"ExplorerHotkeyStealer: Relaunched explorer.exe (new PID={relaunched?.Id.ToString() ?? "unknown"}); GetShellWindow()=0x{GetShellWindow():X} immediately after Process.Start (expected 0x0 - the new process hasn't initialized yet)");

                bool becameShell = WaitForShellWindow(out IntPtr shellWindow, out TimeSpan waited);
                if (becameShell)
                {
                    GetWindowThreadProcessId(shellWindow, out uint shellPid);
                    ShellLogger.Info($"ExplorerHotkeyStealer: GetShellWindow() became non-zero (0x{shellWindow:X}, PID={shellPid}) after waiting {waited.TotalMilliseconds:F0}ms - explorer.exe registered itself as shell");
                }
                else
                {
                    ShellLogger.Warning($"ExplorerHotkeyStealer: Timed out after {ShellWaitTimeout.TotalSeconds}s waiting for relaunched explorer.exe to register as shell; continuing anyway - IsAppRunningAsShell may be computed incorrectly and explorer may open as a browser window");
                }
                ShellTrayWindowDiagnostics.LogShellTrayWindows("ExplorerHotkeyStealer: after waiting for shell registration");

                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"ExplorerHotkeyStealer: Failed to steal Win+B from explorer.exe - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Blocks (on the calling thread, synchronously - this runs before the WPF dispatcher loop
        /// starts) until GetShellWindow() reports a registered shell, or <see cref="ShellWaitTimeout"/>
        /// elapses. Guarantees the relaunched explorer.exe has either won the shell-registration race
        /// or we've given up waiting, before any caller proceeds to create more windows.
        /// </summary>
        private static bool WaitForShellWindow(out IntPtr shellWindow, out TimeSpan waited)
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                shellWindow = GetShellWindow();
                if (shellWindow != IntPtr.Zero)
                {
                    waited = stopwatch.Elapsed;
                    return true;
                }

                Thread.Sleep(ShellWaitPollIntervalMs);
            } while (stopwatch.Elapsed < ShellWaitTimeout);

            shellWindow = IntPtr.Zero;
            waited = stopwatch.Elapsed;
            return false;
        }
    }
}
