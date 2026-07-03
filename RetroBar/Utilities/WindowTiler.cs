using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;

namespace RetroBar.Utilities
{
    // Tiles a task group's windows using Windows' built-in Aero Snap, since there is no public API
    // to invoke a snap directly - only simulated Win+Arrow keystrokes reach it. Holding Left Ctrl
    // down across the Windows-key release (instead of releasing both at once) suppresses the Snap
    // Assist picker that otherwise pops up asking which window should fill the other half.
    public static class WindowTiler
    {
        public enum TilePosition
        {
            Left,
            Right,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
        }

        private const int KeyStepDelayMs = 60;
        private const int PostMaximizeDelayMs = 150;
        private const int PostTileDelayMs = 60;

        private static readonly TilePosition[] TwoWindowLayout = { TilePosition.Left, TilePosition.Right };
        private static readonly TilePosition[] ThreeWindowLayout = { TilePosition.Left, TilePosition.TopRight, TilePosition.BottomRight };
        private static readonly TilePosition[] FourWindowLayout = { TilePosition.TopLeft, TilePosition.TopRight, TilePosition.BottomLeft, TilePosition.BottomRight };

        // Windows' keyboard-driven snap only goes down to quarters, so groups larger than 4 have
        // no layout to generalize to and are left untouched.
        public static async Task TileGroupAsync(IReadOnlyList<ApplicationWindow> windows)
        {
            if (windows == null || windows.Count < 2 || windows.Count > 4) return;

            TilePosition[] layout = windows.Count switch
            {
                2 => TwoWindowLayout,
                3 => ThreeWindowLayout,
                _ => FourWindowLayout,
            };

            for (int i = 0; i < layout.Length; i++)
            {
                await TileWindowAsync(windows[i], layout[i]);
            }
        }

        public static async Task TileWindowAsync(ApplicationWindow window, TilePosition position)
        {
            if (window == null) return;

            window.BringToFront();
            window.Maximize();
            await Task.Delay(PostMaximizeDelayMs);

            switch (position)
            {
                case TilePosition.Left:
                    await SendSnapKeyAsync(NativeMethods.VK.LEFT);
                    break;
                case TilePosition.Right:
                    await SendSnapKeyAsync(NativeMethods.VK.RIGHT);
                    break;
                case TilePosition.TopLeft:
                    await SendSnapKeyAsync(NativeMethods.VK.LEFT);
                    await SendSnapKeyAsync(NativeMethods.VK.UP);
                    break;
                case TilePosition.BottomLeft:
                    await SendSnapKeyAsync(NativeMethods.VK.LEFT);
                    await SendSnapKeyAsync(NativeMethods.VK.DOWN);
                    break;
                case TilePosition.TopRight:
                    await SendSnapKeyAsync(NativeMethods.VK.RIGHT);
                    await SendSnapKeyAsync(NativeMethods.VK.UP);
                    break;
                case TilePosition.BottomRight:
                    await SendSnapKeyAsync(NativeMethods.VK.RIGHT);
                    await SendSnapKeyAsync(NativeMethods.VK.DOWN);
                    break;
            }

            await Task.Delay(PostTileDelayMs);
        }

        private static async Task SendSnapKeyAsync(NativeMethods.VK arrow)
        {
            SendKeyDown(NativeMethods.VK.LWIN);
            await Task.Delay(KeyStepDelayMs);

            SendKeyDown(arrow);
            await Task.Delay(KeyStepDelayMs);
            SendKeyUp(arrow);
            await Task.Delay(KeyStepDelayMs);

            // Pressing Ctrl before releasing Win (rather than releasing both together) is what
            // tells Windows to skip Snap Assist.
            SendKeyDown(NativeMethods.VK.LCONTROL);
            await Task.Delay(KeyStepDelayMs);

            SendKeyUp(NativeMethods.VK.LWIN);
            await Task.Delay(KeyStepDelayMs);
            SendKeyUp(NativeMethods.VK.LCONTROL);
            await Task.Delay(KeyStepDelayMs);
        }

        private static void SendKeyDown(NativeMethods.VK vk) => SendKey(vk, false);

        private static void SendKeyUp(NativeMethods.VK vk) => SendKey(vk, true);

        private static void SendKey(NativeMethods.VK vk, bool keyUp)
        {
            var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
            input.mkhi.ki.wVk = (ushort)vk;
            input.mkhi.ki.wScan = 0;
            input.mkhi.ki.dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;
            input.mkhi.ki.time = 0;
            input.mkhi.ki.dwExtraInfo = NativeMethods.GetMessageExtraInfo();

            NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }
    }
}
