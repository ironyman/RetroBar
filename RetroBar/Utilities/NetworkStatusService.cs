using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    internal class NetworkStatusService : IDisposable
    {
        public enum ConnectionType { None, WiFi, Ethernet }

        public ConnectionType Type { get; private set; } = ConnectionType.None;
        public bool IsConnected { get; private set; }
        public int SignalQuality { get; private set; }
        public string SSID { get; private set; } = string.Empty;

        public event EventHandler StateChanged;

        private IntPtr _wlanHandle = IntPtr.Zero;
        private WlanNotificationCallback _wlanCallback;
        private readonly Dispatcher _dispatcher;

        public NetworkStatusService(Dispatcher dispatcher = null)
        {
            _dispatcher = dispatcher;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            TryInitWlan();
            Refresh();
        }

        private void TryInitWlan()
        {
            try
            {
                uint negVer;
                if (WlanOpenHandle(2, IntPtr.Zero, out negVer, out _wlanHandle) != 0)
                {
                    _wlanHandle = IntPtr.Zero;
                    return;
                }
                _wlanCallback = (ref WLAN_NOTIFICATION_DATA data, IntPtr ctx) =>
                {
                    if (_dispatcher != null)
                        _dispatcher.InvokeAsync(Refresh);
                    else
                        Refresh();
                };
                WlanRegisterNotification(_wlanHandle, WLAN_NOTIFICATION_SOURCE_ALL, true,
                    _wlanCallback, IntPtr.Zero, IntPtr.Zero, out _);
            }
            catch
            {
                _wlanHandle = IntPtr.Zero;
            }
        }

        private void OnNetworkChanged(object sender, EventArgs e)
        {
            if (_dispatcher != null)
                _dispatcher.InvokeAsync(Refresh);
            else
                Refresh();
        }

        public void Refresh()
        {
            var (type, connected) = GetConnectionInfo();
            int quality = 0;
            string ssid = string.Empty;

            if (type == ConnectionType.WiFi && _wlanHandle != IntPtr.Zero)
                GetWifiDetails(out quality, out ssid);

            bool changed = Type != type || IsConnected != connected
                || SignalQuality != quality || SSID != ssid;

            Type = type;
            IsConnected = connected;
            SignalQuality = quality;
            SSID = ssid;

            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private (ConnectionType type, bool connected) GetConnectionInfo()
        {
            bool hasEthernet = false, hasWifi = false;
            bool ethernetUp = false, wifiUp = false;

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    bool up = nic.OperationalStatus == OperationalStatus.Up;
                    bool hasGateway = false;
                    if (up)
                    {
                        foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                        {
                            if (gw.Address.ToString() != "0.0.0.0")
                            {
                                hasGateway = true;
                                break;
                            }
                        }
                    }

                    bool isEthernet =
                        nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx;

                    if (isEthernet)
                    {
                        hasEthernet = true;
                        if (up && hasGateway) ethernetUp = true;
                    }
                    else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        hasWifi = true;
                        if (up && hasGateway) wifiUp = true;
                    }
                }
            }
            catch { }

            if (ethernetUp) return (ConnectionType.Ethernet, true);
            if (wifiUp) return (ConnectionType.WiFi, true);
            if (hasEthernet) return (ConnectionType.Ethernet, false);
            if (hasWifi) return (ConnectionType.WiFi, false);
            return (ConnectionType.None, false);
        }

        private void GetWifiDetails(out int quality, out string ssid)
        {
            quality = 0;
            ssid = string.Empty;
            if (_wlanHandle == IntPtr.Zero) return;

            try
            {
                if (WlanEnumInterfaces(_wlanHandle, IntPtr.Zero, out IntPtr ifList) != 0) return;
                try
                {
                    var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST_HEADER>(ifList);
                    IntPtr itemPtr = ifList + 8;

                    for (int i = 0; i < header.dwNumberOfItems; i++)
                    {
                        var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPtr);
                        if (info.isState == WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                        {
                            var opcode = WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection;
                            if (WlanQueryInterface(_wlanHandle, ref info.InterfaceGuid, opcode,
                                IntPtr.Zero, out _, out IntPtr data, IntPtr.Zero) == 0)
                            {
                                try
                                {
                                    var attrs = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                                    quality = (int)attrs.wlanAssociationAttributes.wlanSignalQuality;
                                    uint len = attrs.wlanAssociationAttributes.dot11Ssid.uSSIDLength;
                                    if (len > 0 && len <= 32)
                                        ssid = Encoding.UTF8.GetString(
                                            attrs.wlanAssociationAttributes.dot11Ssid.ucSSID, 0, (int)len);
                                }
                                finally { WlanFreeMemory(data); }
                            }
                            break;
                        }
                        itemPtr += Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                    }
                }
                finally { WlanFreeMemory(ifList); }
            }
            catch { }
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            if (_wlanHandle != IntPtr.Zero)
            {
                WlanCloseHandle(_wlanHandle, IntPtr.Zero);
                _wlanHandle = IntPtr.Zero;
            }
        }

        // --- P/Invoke ---

        private const uint WLAN_NOTIFICATION_SOURCE_ALL = 0xFFFF;

        private delegate void WlanNotificationCallback(ref WLAN_NOTIFICATION_DATA data, IntPtr context);

        [DllImport("wlanapi.dll", SetLastError = false)]
        private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
            out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = false)]
        private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = false)]
        private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = false)]
        private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            WLAN_INTF_OPCODE OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
            IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll", SetLastError = false)]
        private static extern uint WlanRegisterNotification(IntPtr hClientHandle, uint dwNotifSource,
            [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate,
            WlanNotificationCallback funcCallback, IntPtr pCallbackContext, IntPtr pReserved,
            out uint pdwPrevNotifSource);

        [DllImport("wlanapi.dll", SetLastError = false)]
        private static extern void WlanFreeMemory(IntPtr pMemory);

        private enum WLAN_INTF_OPCODE { wlan_intf_opcode_current_connection = 7 }

        private enum WLAN_INTERFACE_STATE
        {
            wlan_interface_state_not_ready,
            wlan_interface_state_connected,
            wlan_interface_state_ad_hoc_network_formed,
            wlan_interface_state_disconnecting,
            wlan_interface_state_disconnected,
            wlan_interface_state_associating,
            wlan_interface_state_discovering,
            wlan_interface_state_authenticating
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_NOTIFICATION_DATA
        {
            public uint NotificationSource;
            public uint NotificationCode;
            public Guid InterfaceGuid;
            public uint dwDataSize;
            public IntPtr pData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_INTERFACE_INFO_LIST_HEADER
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;          // 36 bytes (4 + 32)
            public uint dot11BssType;              // 4
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;              // 6
            private ushort _reserved;              // 2
            public uint dot11PhyType;              // 4
            public uint uDot11PhyIndex;            // 4
            public uint wlanSignalQuality;         // 4  (offset 56 from start of struct)
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_ATTRIBUTES
        {
            public uint isState;
            public uint wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;          // 512 bytes (256 WCHARs)
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;  // offset 520
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }
    }
}
