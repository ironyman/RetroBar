using System;
using ManagedShell.Common.Logging;
using RetroBar.Utilities;

namespace RetroBar
{
    internal sealed class Program
    {
        private const string MutexName = "RetroBar";
        private const int MutexAttempts = 10;
        private const int MutexWaitMs = 1000;

        private static System.Threading.Mutex _retroBarMutex;

        /// <summary>
        /// The main entry point for the application
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            if (!SingleInstanceCheck())
            {
                return 1;
            }

            App app = new App();
            app.InitializeComponent();

            return app.Run();
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            string dumpPath = MiniDumpHelper.Write();
            string location = dumpPath != null ? dumpPath : MiniDumpHelper.DumpDirectory;
            ShellLogger.Error($"Unhandled exception on background thread — crash dump: {location}\n{e.ExceptionObject}");
        }

        private static bool GetMutex()
        {
            _retroBarMutex = new System.Threading.Mutex(true, MutexName, out bool ok);

            return ok;
        }

        private static bool SingleInstanceCheck()
        {
            for (int i = 0; i < MutexAttempts; i++)
            {
                if (!GetMutex())
                {
                    // Dispose the mutex, otherwise it will never create new
                    _retroBarMutex.Dispose();
                    System.Threading.Thread.Sleep(MutexWaitMs);
                }
                else
                {
                    return true;
                }
            }

            return false;
        }
    }
}
