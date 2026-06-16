using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ManagedShell.Common.Logging;

namespace RetroBar.Utilities
{
    internal static class MiniDumpHelper
    {
        [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        // MiniDumpWithFullMemory
        private const uint DumpType = 0x00000002;

        public static string DumpDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RetroBar", "CrashDumps");

        public static string Write()
        {
            try
            {
                Directory.CreateDirectory(DumpDirectory);
                string path = Path.Combine(DumpDirectory,
                    $"RetroBar_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");

                using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                var proc = Process.GetCurrentProcess();
                bool ok = MiniDumpWriteDump(proc.Handle, (uint)proc.Id, fs.SafeFileHandle, DumpType,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (ok)
                {
                    ShellLogger.Error($"MiniDumpHelper: Crash dump written to {path}");
                    return path;
                }

                int err = Marshal.GetLastWin32Error();
                ShellLogger.Error($"MiniDumpHelper: MiniDumpWriteDump failed, Win32 error {err}");
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"MiniDumpHelper: Failed to write crash dump: {ex.Message}");
            }

            return null;
        }
    }
}
