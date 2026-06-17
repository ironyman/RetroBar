using ManagedShell.Common.Logging;
using System.Windows.Forms;
using static ManagedShell.Interop.NativeMethods;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Reserves Win+B before any other RetroBar/ManagedShell initialization runs, so that if
    /// ExplorerHotkeyStealer needs to kill and relaunch explorer.exe to free the hotkey, it does so
    /// while no Shell_TrayWnd exists at all - neither Explorer's nor ManagedShell's fake one - so
    /// the relaunched explorer.exe takes over as the desktop shell instead of concluding a shell is
    /// already running and opening a File Explorer window.
    ///
    /// The reservation is held on a throwaway window until <see cref="Release"/> is called, which
    /// HotkeyManager does immediately before performing its real registration, keeping the gap
    /// where nothing holds Win+B as short as possible.
    /// </summary>
    public sealed class EarlyWinBReservation
    {
        private const int HOTKEY_ID = 1;
        private ReservationWindow _window = new ReservationWindow();

        public bool IsHeld { get; private set; }

        public EarlyWinBReservation()
        {
            ShellLogger.Info("EarlyWinBReservation: Attempting to reserve Win+B before any tray window exists");

            TraySoftUnregister.TryUnregisterFromProcess("ShellExperienceHost", VK.KEY_B);
            TraySoftUnregister.TryUnregisterFromProcess("sihost", VK.KEY_B);
            TraySoftUnregister.TryUnregisterFromProcess("explorer", VK.KEY_B);

            IsHeld = TryRegister();
            if (IsHeld)
            {
                ShellLogger.Info("EarlyWinBReservation: Reserved Win+B without restarting explorer.exe");
                return;
            }

            ExplorerHotkeyStealer.StealWinBFromExplorer(() => IsHeld = TryRegister());

            ShellLogger.Info(IsHeld
                ? "EarlyWinBReservation: Reserved Win+B after restarting explorer.exe"
                : "EarlyWinBReservation: Failed to reserve Win+B even after restarting explorer.exe");
        }

        private bool TryRegister() =>
            RegisterHotKey(_window.Handle, HOTKEY_ID, (uint)(MOD.WIN | MOD.NOREPEAT), (uint)VK.KEY_B);

        /// <summary>
        /// Releases the reservation. Call immediately before registering Win+B for real.
        /// </summary>
        public void Release()
        {
            if (_window == null) return;

            if (IsHeld)
                UnregisterHotKey(_window.Handle, HOTKEY_ID);

            _window.DestroyHandle();
            _window = null;
            IsHeld = false;
        }

        private sealed class ReservationWindow : NativeWindow
        {
            public ReservationWindow() => CreateHandle(new CreateParams());
        }
    }
}
