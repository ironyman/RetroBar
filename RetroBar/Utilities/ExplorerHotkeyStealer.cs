using ManagedShell.Common.Logging;
using System;
using System.Diagnostics;
using System.IO;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Steals Win+B from explorer.exe by killing it, registering the hotkey while no process is
    /// alive to hold it, then relaunching explorer.exe. Unlike sihost.exe, explorer.exe does not
    /// auto-restart when killed (TerminateProcess doesn't trigger its crash-recovery registration),
    /// so this is deterministic: once it has fully exited, nothing can be holding the hotkey, and we
    /// control exactly when it comes back.
    /// </summary>
    internal static class ExplorerHotkeyStealer
    {
        private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

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

                foreach (var p in existing)
                {
                    try { p.Kill(); p.WaitForExit(5000); }
                    catch (Exception ex) { ShellLogger.Warning($"ExplorerHotkeyStealer: Failed to kill explorer.exe (PID={p.Id}) - {ex.Message}"); }
                    finally { p.Dispose(); }
                }

                ShellLogger.Info("ExplorerHotkeyStealer: Killed explorer.exe, registering Win+B while it's dead");
                registerHotkey();

                Process.Start(ExplorerPath);
                ShellLogger.Info("ExplorerHotkeyStealer: Relaunched explorer.exe");

                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"ExplorerHotkeyStealer: Failed to steal Win+B from explorer.exe - {ex.Message}");
                return false;
            }
        }
    }
}
