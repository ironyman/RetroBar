using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Sends WMTRAY_UNREGISTERHOTKEY to a process's windows for a given hotkey, mirroring what
    /// Explorer's own tray window does to release a hotkey, but targeting arbitrary shell processes
    /// (e.g. ShellExperienceHost, sihost) that may also be holding it.
    /// </summary>
    internal static class TraySoftUnregister
    {
        private const int WMTRAY_UNREGISTERHOTKEY = (int)WM.USER + 231;

        public static void TryUnregisterFromProcess(string processName, VK key)
        {
            try
            {
                // Collect one representative window handle per process instance (pid -> hwnd)
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
                        ShellLogger.Debug($"TraySoftUnregister: {processName} hotkey table is empty");
                        continue;
                    }

                    int idx = table.FindIndex(e => e.VirtualKey == (byte)key && (e.Modifier & (byte)MOD.WIN) != 0);
                    if (idx < 0)
                    {
                        ShellLogger.Debug($"TraySoftUnregister: {key} not found in {processName} binary hotkey table");
                        continue;
                    }

                    int hotkeyId = table[idx].Id;
                    ShellLogger.Debug($"TraySoftUnregister: {key} found in {processName} binary at table ID={hotkeyId}; sending WMTRAY_UNREGISTERHOTKEY to all its windows");

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
                ShellLogger.Warning($"TraySoftUnregister: Exception unregistering {key} from {processName} - {ex.Message}");
            }
        }
    }
}
