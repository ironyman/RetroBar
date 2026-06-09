using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace RetroBar.Utilities
{
    internal class NetworkTrayIcon : IDisposable
    {
        public const string GUID_STRING = "c8cac6d4-64e8-4afe-b5d6-1c4e1c9f3fb3";
        private static readonly Guid ICON_GUID = new Guid(GUID_STRING);

        private const int WM_TRAYICON_CALLBACK = 0x8001; // WM_APP + 1
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_LBUTTONUP = 0x0202;
        private const uint NIM_ADD = 0;
        private const uint NIM_MODIFY = 1;
        private const uint NIM_DELETE = 2;
        private const uint NIM_SETVERSION = 4;
        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_TIP = 0x04;
        private const uint NIF_GUID = 0x20;
        private const uint NOTIFYICON_VERSION_4 = 4;

        private readonly NetworkStatusService _service;
        private readonly TrayMessageWindow _msgWindow;
        private System.Drawing.Icon _currentIcon;
        private bool _added;

        public NetworkTrayIcon()
        {
            uint wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _msgWindow = new TrayMessageWindow(this, wmTaskbarCreated);
            _service = new NetworkStatusService();
            _service.StateChanged += OnStateChanged;
            AddIcon();
            UpdateIconAndTip();
        }

        private void OnStateChanged(object sender, EventArgs e) => UpdateIconAndTip();

        private void AddIcon()
        {
            var nid = CreateNid();
            nid.uFlags = NIF_MESSAGE | NIF_GUID;
            if (Shell_NotifyIcon(NIM_ADD, ref nid))
            {
                nid.uVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIcon(NIM_SETVERSION, ref nid);
                _added = true;
            }
        }

        private void UpdateIconAndTip()
        {
            if (!_added) return;

            bool light = IsWindowsLightTheme();
            string iconName = GetIconName(_service.Type, _service.IsConnected, _service.SignalQuality, light);
            string tip = GetTooltipText();

            var newIcon = LoadIcon(iconName);
            _currentIcon?.Dispose();
            _currentIcon = newIcon;

            var nid = CreateNid();
            nid.uFlags = NIF_ICON | NIF_TIP | NIF_GUID;
            nid.hIcon = _currentIcon?.Handle ?? IntPtr.Zero;
            nid.szTip = tip.Length > 127 ? tip.Substring(0, 127) : tip;
            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }

        internal void HandleClick()
        {
            string uri = _service?.Type == NetworkStatusService.ConnectionType.WiFi
                ? "ms-settings:network-wifi"
                : "ms-settings:network-ethernet";
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { }
        }

        internal void HandleTaskbarCreated()
        {
            _added = false;
            AddIcon();
            UpdateIconAndTip();
        }

        private NOTIFYICONDATA CreateNid() => new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _msgWindow.Handle,
            uCallbackMessage = WM_TRAYICON_CALLBACK,
            guidItem = ICON_GUID
        };

        private static string GetIconName(
            NetworkStatusService.ConnectionType type, bool connected, int quality, bool lightTheme)
        {
            int bars = Math.Max(0, Math.Min(4, quality / 20));
            switch (type)
            {
                case NetworkStatusService.ConnectionType.WiFi:
                    if (!connected) return lightTheme ? "6303" : "6301";
                    return ((lightTheme ? 6020 : 6000) + bars).ToString();
                case NetworkStatusService.ConnectionType.Ethernet:
                    if (!connected) return lightTheme ? "6204" : "6201";
                    return lightTheme ? "6203" : "6200";
                default:
                    return lightTheme ? "6303" : "6301";
            }
        }

        private string GetTooltipText()
        {
            switch (_service.Type)
            {
                case NetworkStatusService.ConnectionType.WiFi:
                    if (!_service.IsConnected) return "Wi-Fi: Disconnected";
                    string name = string.IsNullOrEmpty(_service.SSID) ? "Wi-Fi" : _service.SSID;
                    return $"{name} ({_service.SignalQuality}%)";
                case NetworkStatusService.ConnectionType.Ethernet:
                    return _service.IsConnected ? "Ethernet: Connected" : "Ethernet: Unplugged";
                default:
                    return "Not connected";
            }
        }

        private static System.Drawing.Icon LoadIcon(string name)
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/RetroBar;component/Icons/Network/{name}.ico");
                var stream = Application.GetResourceStream(uri)?.Stream;
                if (stream != null)
                {
                    int size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
                    return new System.Drawing.Icon(stream, size, size);
                }
            }
            catch { }
            return null;
        }

        private static bool IsWindowsLightTheme()
        {
            try
            {
                object v = Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                    ?.GetValue("SystemUsesLightTheme");
                return v is int i && i == 1;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            _service.StateChanged -= OnStateChanged;
            _service.Dispose();

            if (_added)
            {
                var nid = CreateNid();
                nid.uFlags = NIF_GUID;
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _added = false;
            }

            _currentIcon?.Dispose();
            _msgWindow.ReleaseHandle();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        private sealed class TrayMessageWindow : System.Windows.Forms.NativeWindow
        {
            private readonly NetworkTrayIcon _owner;
            private readonly uint _wmTaskbarCreated;

            public TrayMessageWindow(NetworkTrayIcon owner, uint wmTaskbarCreated)
            {
                _owner = owner;
                _wmTaskbarCreated = wmTaskbarCreated;
                CreateHandle(new System.Windows.Forms.CreateParams { Parent = new IntPtr(-3) });
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_TRAYICON_CALLBACK)
                {
                    int evt = (int)(m.LParam.ToInt64() & 0xFFFF);
                    if (evt == WM_LBUTTONDBLCLK || evt == WM_LBUTTONUP)
                        _owner.HandleClick();
                }
                else if (_wmTaskbarCreated != 0 && (uint)m.Msg == _wmTaskbarCreated)
                {
                    _owner.HandleTaskbarCreated();
                }
                base.WndProc(ref m);
            }
        }
    }
}
