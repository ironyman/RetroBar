using ManagedShell.Common.Logging;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Kills sihost.exe and races its respawn so RetroBar can RegisterHotKey for Win+B before
    /// sihost gets a chance to re-claim it. Two strategies are available, picked at compile time
    /// via the SIHOST_RACE_IFEO_DEBUGGER constant (see RetroBar.csproj):
    ///
    ///   - Not defined (default): tight-poll for the new sihost PID and NtSuspendProcess it before
    ///     calling the caller's register action. Best-effort; no persistent system changes.
    ///
    ///   - Defined: hijacks sihost.exe's launch via Image File Execution Options so RetroBar
    ///     relaunches it under DEBUG_ONLY_THIS_PROCESS, which deterministically halts it before its
    ///     first instruction runs. Requires RetroBar to be running elevated (HKLM write), and
    ///     briefly leaves an IFEO registry key in place for the duration of the race.
    /// </summary>
    internal static class SihostRacer
    {
        private const string SihostExeName = "sihost";
        internal const string DebuggerStubArg = "--sihost-debugger";
        private const string IfeoKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sihost.exe";
        private const string FrozenEventName = "RetroBar_SihostFrozen";
        private const string RaceDoneEventName = "RetroBar_SihostRaceDone";

        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);
        private const uint PROCESS_SUSPEND_RESUME = 0x0800;

        /// <summary>
        /// Kills the running sihost.exe and invokes <paramref name="registerHotkey"/> while the
        /// respawned instance is prevented from running ahead of us. Returns true if the race was
        /// won deterministically (the new sihost was actually held back before registerHotkey ran).
        /// </summary>
        public static bool StealWinBFromSihost(Action registerHotkey)
        {
            try
            {
                var existing = Process.GetProcessesByName(SihostExeName);
                if (existing.Length == 0)
                {
                    ShellLogger.Warning("SihostRacer: sihost.exe not running, nothing to race");
                    return false;
                }

#if SIHOST_RACE_IFEO_DEBUGGER
                return RaceViaDebuggerHijack(existing, registerHotkey);
#else
                return RaceViaSuspendPoll(existing, registerHotkey);
#endif
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"SihostRacer: Failed to race sihost - {ex.Message}");
                return false;
            }
        }

        #region Suspend-on-creation poll (default)
        private static bool RaceViaSuspendPoll(Process[] existing, Action registerHotkey)
        {
            int[] oldPids = Array.ConvertAll(existing, p => p.Id);
            foreach (var p in existing)
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception ex) { ShellLogger.Warning($"SihostRacer: Failed to kill sihost.exe (PID={p.Id}) - {ex.Message}"); }
                finally { p.Dispose(); }
            }

            ShellLogger.Info("SihostRacer: Killed sihost.exe, polling for respawn");

            var originalPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            IntPtr suspended = IntPtr.Zero;
            int newPid = -1;
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < 5000 && suspended == IntPtr.Zero)
                {
                    foreach (var p in Process.GetProcessesByName(SihostExeName))
                    {
                        int pid = p.Id;
                        p.Dispose();
                        if (Array.IndexOf(oldPids, pid) >= 0) continue;

                        IntPtr handle = OpenProcess((ProcessAccessFlags)PROCESS_SUSPEND_RESUME, false, pid);
                        if (handle == IntPtr.Zero) continue;

                        if (NtSuspendProcess(handle) == 0)
                        {
                            suspended = handle;
                            newPid = pid;
                            break;
                        }
                        CloseHandle(handle);
                    }
                }
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
            }

            if (suspended == IntPtr.Zero)
            {
                ShellLogger.Warning("SihostRacer: Could not catch respawned sihost.exe in time; registering blind");
                registerHotkey();
                return false;
            }

            try
            {
                ShellLogger.Info($"SihostRacer: Suspended new sihost.exe (PID={newPid}) before it could register hotkeys");
                registerHotkey();
            }
            finally
            {
                NtResumeProcess(suspended);
                CloseHandle(suspended);
            }

            return true;
        }
        #endregion

        #region IFEO debugger hijack
#if SIHOST_RACE_IFEO_DEBUGGER
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ContinueDebugEvent(int dwProcessId, int dwThreadId, uint dwContinueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcessStop(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugSetProcessKillOnExit(bool killOnExit);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        // Only dwDebugEventCode/dwProcessId/dwThreadId are read; the trailing union is large enough
        // to hold any DEBUG_EVENT variant (EXCEPTION_DEBUG_INFO is the biggest on x64) and otherwise unused.
        [StructLayout(LayoutKind.Sequential)]
        private struct DEBUG_EVENT
        {
            public int dwDebugEventCode;
            public int dwProcessId;
            public int dwThreadId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
            public byte[] u;
        }

        private const uint DEBUG_ONLY_THIS_PROCESS = 0x00000002;
        private const int CREATE_PROCESS_DEBUG_EVENT = 3;
        private const uint DBG_CONTINUE = 0x00010002;
        private const uint INFINITE = 0xFFFFFFFF;

        private static bool RaceViaDebuggerHijack(Process[] existing, Action registerHotkey)
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string debuggerValue = $"\"{exePath}\" {DebuggerStubArg}";

            using var frozen = new EventWaitHandle(false, EventResetMode.AutoReset, FrozenEventName);
            using var raceDone = new EventWaitHandle(false, EventResetMode.AutoReset, RaceDoneEventName);

            try
            {
                using (var key = Registry.LocalMachine.CreateSubKey(IfeoKeyPath))
                {
                    key.SetValue("Debugger", debuggerValue, RegistryValueKind.String);
                }
                ShellLogger.Info("SihostRacer: Installed IFEO debugger hijack for sihost.exe");

                foreach (var p in existing)
                {
                    try { p.Kill(); p.WaitForExit(5000); }
                    catch (Exception ex) { ShellLogger.Warning($"SihostRacer: Failed to kill sihost.exe (PID={p.Id}) - {ex.Message}"); }
                    finally { p.Dispose(); }
                }

                ShellLogger.Info("SihostRacer: Killed sihost.exe, waiting for debugger stub to halt the respawn");

                if (!frozen.WaitOne(5000))
                {
                    ShellLogger.Warning("SihostRacer: Debugger stub did not signal in time; registering blind");
                    registerHotkey();
                    return false;
                }

                ShellLogger.Info("SihostRacer: Respawned sihost.exe halted before its first instruction; registering hotkey");
                registerHotkey();
                return true;
            }
            finally
            {
                raceDone.Set();

                try
                {
                    Registry.LocalMachine.DeleteSubKey(IfeoKeyPath, throwOnMissingSubKey: false);
                    ShellLogger.Info("SihostRacer: Removed IFEO debugger hijack for sihost.exe");
                }
                catch (Exception ex)
                {
                    ShellLogger.Warning($"SihostRacer: Failed to remove IFEO hijack key, remove manually from HKLM\\{IfeoKeyPath} - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Entry point used when RetroBar.exe is re-launched by Windows in place of sihost.exe via
        /// the IFEO Debugger key. Relaunches the real sihost.exe under DEBUG_ONLY_THIS_PROCESS, halts
        /// it at the loader breakpoint (before any of its code runs), signals the waiting RetroBar
        /// instance, then detaches so sihost continues running normally.
        /// </summary>
        public static int RunDebuggerStub(string[] args)
        {
            if (args.Length < 2)
            {
                ShellLogger.Warning("SihostRacer: Debugger stub invoked without a target image path");
                return 1;
            }

            string targetPath = args[1];
            string commandLine = $"\"{targetPath}\"" + (args.Length > 2 ? " " + string.Join(" ", args, 2, args.Length - 2) : "");

            var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, DEBUG_ONLY_THIS_PROCESS,
                IntPtr.Zero, null, ref startupInfo, out var processInfo))
            {
                ShellLogger.Warning($"SihostRacer: Debugger stub failed to relaunch {targetPath} (Error: {Marshal.GetLastWin32Error()})");
                return 1;
            }

            try
            {
                using var frozen = new EventWaitHandle(false, EventResetMode.AutoReset, FrozenEventName);
                using var raceDone = new EventWaitHandle(false, EventResetMode.AutoReset, RaceDoneEventName);

                DebugSetProcessKillOnExit(false);

                bool signaled = false;
                while (WaitForDebugEvent(out var debugEvent, 5000))
                {
                    if (!signaled && debugEvent.dwDebugEventCode == CREATE_PROCESS_DEBUG_EVENT)
                    {
                        frozen.Set();
                        signaled = true;
                        raceDone.WaitOne(5000);
                        ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, DBG_CONTINUE);
                        DebugActiveProcessStop(debugEvent.dwProcessId);
                        break;
                    }

                    ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, DBG_CONTINUE);
                }

                if (!signaled)
                {
                    ShellLogger.Warning("SihostRacer: Debugger stub never observed CREATE_PROCESS_DEBUG_EVENT");
                }

                return 0;
            }
            finally
            {
                CloseHandle(processInfo.hThread);
                CloseHandle(processInfo.hProcess);
            }
        }
#endif
        #endregion
    }
}
