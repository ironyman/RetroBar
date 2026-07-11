using System;
using ManagedShell;
using RetroBar.Utilities;
using System.Windows;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using Application = System.Windows.Application;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell.Common.Enums;
using System.Diagnostics;
using System.Reflection;
using ManagedShell.Common.Logging;
using System.Linq;

namespace RetroBar
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private bool _errorVisible;
        private ManagedShellLogger _logger;
        private WindowManager _windowManager;
        private NetworkTrayIcon _networkTrayIcon;

        private readonly DictionaryManager _dictionaryManager;
        private readonly ExplorerMonitor _explorerMonitor;
        private readonly ShellManager _shellManager;
        private readonly StartMenuMonitor _startMenuMonitor;
        private readonly Updater _updater;
        private readonly HotkeyManager _hotkeyManager;
        private readonly EarlyWinBReservation _earlyWinBReservation;

        public App()
        {
            // Reserve Win+B before anything else initializes, so that if explorer.exe needs to be
            // killed and relaunched to free it, no Shell_TrayWnd (real or ManagedShell's fake one)
            // exists yet and the relaunched explorer.exe correctly takes over as the desktop shell.
            _earlyWinBReservation = new EarlyWinBReservation();

            _shellManager = SetupManagedShell();
            _shellManager.TasksService.WindowInsertionIndexProvider = (win, windows) =>
            {
                // A window reappearing after a workspace switch has a remembered slot - restoring
                // it there takes priority over GroupAfterParent, which is only meant for windows
                // that are genuinely new.
                int workspaceIdx = WorkspaceManager.Instance.GetInsertionIndex(win, windows);
                if (workspaceIdx >= 0) return workspaceIdx;

                if (!Settings.Instance.GroupAfterParent) return -1;
                return ParentWindowHelper.FindInsertionIndex(win, windows);
            };

            _explorerMonitor = new ExplorerMonitor();
            _startMenuMonitor = new StartMenuMonitor(new AppVisibilityHelper(false));
            _dictionaryManager = new DictionaryManager();
            _updater = new Updater();
            _hotkeyManager = new HotkeyManager(_earlyWinBReservation);

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
        }

        public void ExitGracefully()
        {
            _shellManager.AppBarManager.SignalGracefulShutdown();
            Current.Shutdown();
        }

        private void App_OnStartup(object sender, StartupEventArgs e)
        {
            if (Settings.Instance.UseSoftwareRendering)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }

            _dictionaryManager.SetLanguageFromSettings();
            loadTheme();
            WorkspaceManager.Instance.Initialize(_shellManager.TasksService.Windows, _shellManager.TasksService);
            _windowManager = new WindowManager(_dictionaryManager, _explorerMonitor, _shellManager, _startMenuMonitor, _updater, _hotkeyManager);
            _networkTrayIcon = new NetworkTrayIcon();
        }

        private void App_OnExit(object sender, ExitEventArgs e)
        {
            ExitApp();
        }

        private void App_OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            ExitApp();
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.UseSoftwareRendering))
            {
                if (Settings.Instance.UseSoftwareRendering)
                {
                    RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                }
                else
                {
                    RenderOptions.ProcessRenderMode = RenderMode.Default;
                }
            }
            else if (e.PropertyName == nameof(Settings.Theme) || e.PropertyName == nameof(Settings.TaskbarScale))
            {
                setTaskIconSize();
            }
        }

        private void loadTheme()
        {
            _dictionaryManager.SetThemeFromSettings();
            setTaskIconSize();
        }

        private void setTaskIconSize()
        {
            bool useLargeIcons = Settings.Instance.TaskbarScale > 1 || (FindResource("UseLargeIcons") as bool? ?? false);

            if (_shellManager.TasksService.TaskIconSize != IconSize.Small != useLargeIcons)
            {
                _shellManager.TasksService.TaskIconSize = useLargeIcons ? IconSize.Large : IconSize.Small;
            }
        }

        private ShellManager SetupManagedShell()
        {
            IntPtr shellWindow = NativeMethods.GetShellWindow();
            EnvironmentHelper.IsAppRunningAsShell = shellWindow == IntPtr.Zero;
            ShellLogger.Info($"App: GetShellWindow()=0x{shellWindow:X} -> IsAppRunningAsShell={EnvironmentHelper.IsAppRunningAsShell} (ExplorerHotkeyStealer waits for this to be non-zero after a relaunch, so 0x0 here normally only happens when no relaunch occurred, or its wait timed out - which wrongly concludes RetroBar is the shell and disables ExplorerHelper's hide-taskbar logic for the rest of the session)");
            Utilities.ShellTrayWindowDiagnostics.LogShellTrayWindows("App.SetupManagedShell: before computing IsAppRunningAsShell");

            _logger = new ManagedShellLogger();

            ShellConfig config = ShellManager.DefaultShellConfig;
            var pinnedList = Settings.Instance.NotifyIconBehaviors
                .Where(setting => setting.Behavior == NotifyIconBehavior.AlwaysShow)
                .Select(setting => setting.Identifier)
                .ToList();

            // Always keep our custom network icon immediately before the volume icon
            var netIdx = pinnedList.IndexOf(NetworkTrayIcon.GUID_STRING);
            var volIdx = pinnedList.IndexOf(ManagedShell.WindowsTray.NotificationArea.VOLUME_GUID);
            if (netIdx >= 0 && volIdx >= 0 && netIdx != volIdx - 1)
            {
                pinnedList.RemoveAt(netIdx);
                volIdx = pinnedList.IndexOf(ManagedShell.WindowsTray.NotificationArea.VOLUME_GUID);
                pinnedList.Insert(volIdx, NetworkTrayIcon.GUID_STRING);
            }

            config.PinnedNotifyIcons = pinnedList.ToArray();

            ShellManager shellManager = new ShellManager(config);
            Utilities.ShellTrayWindowDiagnostics.LogShellTrayWindows("App.SetupManagedShell: after ShellManager ctor (ManagedShell's own Shell_TrayWnd should now exist)");
            return shellManager;
        }

        public void RestartApp()
        {
            try
            {
                // run the program again
                Process current = new Process();
                current.StartInfo.FileName = ExePath.GetExecutablePath();
                current.Start();

                // close this instance
                ExitGracefully();
            }
            catch
            { }
        }

        private void ExitApp()
        {
            WorkspaceManager.Instance.ShowAllWindows();

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

            _networkTrayIcon?.Dispose();
            _explorerMonitor.Dispose();
            _windowManager.Dispose();
            _dictionaryManager.Dispose();
            _shellManager.Dispose();
            _startMenuMonitor.Dispose();
            _updater.Dispose();
            _hotkeyManager.Dispose();
            _logger.Dispose();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            string version = fvi.FileVersion;

            string inner = "";
            if (e.Exception.InnerException != null)
                inner = "\r\n\r\nInner exception:\r\nMessage: " + e.Exception.InnerException.Message + "\r\nTarget Site: " + e.Exception.InnerException.TargetSite + "\r\n\r\n" + e.Exception.InnerException.StackTrace;

            string msg = "Would you like to restart RetroBar?\r\n\r\nPlease submit a bug report with a screenshot of this error. Thanks! \r\nMessage: " + e.Exception.Message + "\r\nTarget Site: " + e.Exception.TargetSite + "\r\nVersion: " + version + "\r\n\r\n" + e.Exception.StackTrace + inner;

            ShellLogger.Error(msg, e.Exception);

            string dumpPath = Utilities.MiniDumpHelper.Write();
            if (dumpPath != null)
                ShellLogger.Error($"Crash dump written to: {dumpPath}");

            string dMsg;

            if (msg.Length > 1000)
                dMsg = msg.Substring(0, 999) + "...";
            else
                dMsg = msg;

            try
            {
                if (!_errorVisible)
                {
                    _errorVisible = true;

                    // Automatically restart for known render thread failure messages.
                    if (e.Exception.Message.StartsWith("UCEERR_RENDERTHREADFAILURE"))
                    {
                        RestartApp();
                        Environment.FailFast("Automatically restarted RetroBar due to a render thread failure.");
                    }
                    else
                    {
                        if (MessageBox.Show(dMsg, "RetroBar Error", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        {
                            // it's like getting a morning coffee.
                            RestartApp();
                            Environment.FailFast("User restarted RetroBar due to an exception.");
                        }
                    }

                    _errorVisible = false;
                }
            }
            catch
            {
                // If this fails we're probably up the creek. Abandon ship!
                ExitGracefully();
            }

            e.Handled = true;
        }
    }
}