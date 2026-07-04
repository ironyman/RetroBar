using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ManagedShell.Common.Logging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Resolves the network adapter currently carrying the default route (destination
    /// 0.0.0.0/0), i.e. the "main" network card, via the IP Helper API routing table.
    /// </summary>
    internal static class NetworkInterfaceResolver
    {
        private const ushort AfInet = 2;

        // MIB_IPFORWARD_ROW2 layout (x64/x86/ARM64 all use natural 8-byte alignment for the
        // leading NET_LUID, so these offsets hold across platforms):
        //   0   NET_LUID InterfaceLuid           (8 bytes)
        //   8   NET_IFINDEX InterfaceIndex        (4 bytes)
        //   12  IP_ADDRESS_PREFIX DestinationPrefix (32 bytes: 28-byte SOCKADDR_INET + 1-byte
        //       PrefixLength, padded to 4-byte alignment)
        //     +0  SOCKADDR_INET.Family (2 bytes)
        //     +28 PrefixLength (1 byte)
        //   44  SOCKADDR_INET NextHop             (28 bytes)
        //   ...  SitePrefixLength, padding
        //   84  ULONG Metric                      (4 bytes)
        //   ... remaining fields (unused here)
        // Total row size is 104 bytes.
        private const int RowSize = 104;
        private const int InterfaceIndexOffset = 8;
        private const int DestinationFamilyOffset = 12;
        private const int DestinationPrefixLengthOffset = 12 + 28;
        private const int MetricOffset = 84;

        // MIB_IPFORWARD_TABLE2 is a ULONG NumEntries followed by the row array; the array
        // is padded to the row struct's 8-byte alignment requirement, so it starts at offset 8.
        private const int RowsOffset = 8;

        public static NetworkInterface GetDefaultRouteInterface()
        {
            int interfaceIndex = GetDefaultRouteInterfaceIndex();
            if (interfaceIndex < 0)
            {
                return null;
            }

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPv4InterfaceProperties ipv4Props = ni.GetIPProperties()?.GetIPv4Properties();
                    if (ipv4Props != null && ipv4Props.Index == interfaceIndex)
                    {
                        return ni;
                    }
                }
                catch
                {
                    // Interfaces without usable IPv4 properties (loopback, IPv6-only, etc.) throw here; skip them.
                }
            }

            return null;
        }

        private static int GetDefaultRouteInterfaceIndex()
        {
            IntPtr table = IntPtr.Zero;
            try
            {
                int result = GetIpForwardTable2(AfInet, out table);
                if (result != 0 || table == IntPtr.Zero)
                {
                    return -1;
                }

                uint numEntries = (uint)Marshal.ReadInt32(table, 0);
                int bestIndex = -1;
                uint bestMetric = uint.MaxValue;

                for (uint i = 0; i < numEntries; i++)
                {
                    int rowOffset = RowsOffset + (int)(i * RowSize);
                    ushort family = (ushort)Marshal.ReadInt16(table, rowOffset + DestinationFamilyOffset);
                    byte prefixLength = Marshal.ReadByte(table, rowOffset + DestinationPrefixLengthOffset);

                    if (family != AfInet || prefixLength != 0)
                    {
                        continue;
                    }

                    uint metric = (uint)Marshal.ReadInt32(table, rowOffset + MetricOffset);
                    if (metric < bestMetric)
                    {
                        bestMetric = metric;
                        bestIndex = Marshal.ReadInt32(table, rowOffset + InterfaceIndexOffset);
                    }
                }

                return bestIndex;
            }
            catch (Exception ex)
            {
                ShellLogger.Error("NetworkInterfaceResolver: Failed to read IP forward table", ex);
                return -1;
            }
            finally
            {
                if (table != IntPtr.Zero)
                {
                    FreeMibTable(table);
                }
            }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIpForwardTable2(ushort family, out IntPtr table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr table);
    }
}
