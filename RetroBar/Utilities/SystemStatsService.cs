using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Polls CPU, memory and (C:) disk activity once per second and keeps a rolling
    /// history of each so tray gauges can render both a live value and a sparkline.
    /// </summary>
    public class SystemStatsService
    {
        private static readonly Lazy<SystemStatsService> _lazy = new Lazy<SystemStatsService>(() => new SystemStatsService());
        public static SystemStatsService Instance => _lazy.Value;

        public const int HistoryCapacity = 60;

        public event Action StatsUpdated;

        public double CpuPercent { get; private set; }
        public double MemoryPercent { get; private set; }
        public double DiskPercent { get; private set; }
        public ulong MemoryUsedBytes { get; private set; }
        public ulong MemoryTotalBytes { get; private set; }

        public double DownloadBytesPerSecond { get; private set; }
        public double UploadBytesPerSecond { get; private set; }
        public double DownloadPercent { get; private set; }
        public double UploadPercent { get; private set; }
        public string NetworkAdapterName { get; private set; }
        public string NetworkIpAddress { get; private set; }

        public Queue<double> CpuHistory { get; } = new Queue<double>(HistoryCapacity);
        public Queue<double> MemoryHistory { get; } = new Queue<double>(HistoryCapacity);
        public Queue<double> DiskHistory { get; } = new Queue<double>(HistoryCapacity);
        public Queue<double> DownloadHistory { get; } = new Queue<double>(HistoryCapacity);
        public Queue<double> UploadHistory { get; } = new Queue<double>(HistoryCapacity);

        private readonly Timer _timer;
        private readonly Dispatcher _dispatcher;
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _diskCounter;

        // Re-resolve the default-route adapter periodically so we notice link changes
        // (switching Wi-Fi networks, plugging in Ethernet, connecting a VPN, etc.).
        private const int NetworkResolveIntervalTicks = 15;
        private int _networkResolveCountdown;
        private NetworkInterface _networkInterface;
        private string _networkAdapterName;
        private string _networkIpAddress;
        private long _lastBytesReceived = -1;
        private long _lastBytesSent = -1;
        private double _maxDownloadBytesPerSecond;
        private double _maxUploadBytesPerSecond;

        private SystemStatsService()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("SystemStatsService: Unable to create CPU counter", ex);
                _cpuCounter = null;
            }

            try
            {
                _diskCounter = new PerformanceCounter("LogicalDisk", "% Disk Time", "C:");
                _diskCounter.NextValue();
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("SystemStatsService: Unable to create disk counter", ex);
                _diskCounter = null;
            }

            _timer = new Timer(1000) { AutoReset = true };
            _timer.Elapsed += Timer_Elapsed;
            _timer.Start();
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            double cpu = SafeNextValue(_cpuCounter);
            double disk = Math.Min(100, SafeNextValue(_diskCounter));

            MEMORYSTATUSEX mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            double memPercent = 0;
            ulong used = 0, total = 0;
            if (GlobalMemoryStatusEx(ref mem))
            {
                memPercent = mem.dwMemoryLoad;
                total = mem.ullTotalPhys;
                used = mem.ullTotalPhys - mem.ullAvailPhys;
            }

            (double downBps, double upBps) = SampleNetwork();
            if (downBps > _maxDownloadBytesPerSecond)
            {
                _maxDownloadBytesPerSecond = downBps;
            }
            if (upBps > _maxUploadBytesPerSecond)
            {
                _maxUploadBytesPerSecond = upBps;
            }

            double downPercent = _maxDownloadBytesPerSecond > 0 ? Math.Min(100, downBps / _maxDownloadBytesPerSecond * 100) : 0;
            double upPercent = _maxUploadBytesPerSecond > 0 ? Math.Min(100, upBps / _maxUploadBytesPerSecond * 100) : 0;

            _dispatcher.Invoke(() =>
            {
                CpuPercent = cpu;
                MemoryPercent = memPercent;
                DiskPercent = disk;
                MemoryUsedBytes = used;
                MemoryTotalBytes = total;
                DownloadBytesPerSecond = downBps;
                UploadBytesPerSecond = upBps;
                DownloadPercent = downPercent;
                UploadPercent = upPercent;
                NetworkAdapterName = _networkAdapterName;
                NetworkIpAddress = _networkIpAddress;

                Enqueue(CpuHistory, cpu);
                Enqueue(MemoryHistory, memPercent);
                Enqueue(DiskHistory, disk);
                Enqueue(DownloadHistory, downPercent);
                Enqueue(UploadHistory, upPercent);

                StatsUpdated?.Invoke();
            });
        }

        private (double downBps, double upBps) SampleNetwork()
        {
            _networkResolveCountdown--;
            if (_networkInterface == null || _networkResolveCountdown <= 0)
            {
                _networkResolveCountdown = NetworkResolveIntervalTicks;
                NetworkInterface resolved = NetworkInterfaceResolver.GetDefaultRouteInterface();
                if (resolved != null && (_networkInterface == null || resolved.Id != _networkInterface.Id))
                {
                    _networkInterface = resolved;
                    _lastBytesReceived = -1;
                    _lastBytesSent = -1;
                    _networkAdapterName = resolved.Name;
                    _networkIpAddress = GetIPv4Address(resolved);
                }
                else if (resolved == null)
                {
                    _networkInterface = null;
                    _networkAdapterName = null;
                    _networkIpAddress = null;
                }
            }

            if (_networkInterface == null)
            {
                return (0, 0);
            }

            try
            {
                IPv4InterfaceStatistics stats = _networkInterface.GetIPv4Statistics();
                long rx = stats.BytesReceived;
                long tx = stats.BytesSent;

                double downBps = 0, upBps = 0;
                if (_lastBytesReceived >= 0)
                {
                    downBps = Math.Max(0, rx - _lastBytesReceived);
                    upBps = Math.Max(0, tx - _lastBytesSent);
                }

                _lastBytesReceived = rx;
                _lastBytesSent = tx;
                return (downBps, upBps);
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("SystemStatsService: Failed to read network statistics", ex);
                _networkInterface = null;
                return (0, 0);
            }
        }

        private static string GetIPv4Address(NetworkInterface ni)
        {
            try
            {
                UnicastIPAddressInformation address = ni.GetIPProperties()?.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                return address?.Address.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static double SafeNextValue(PerformanceCounter counter)
        {
            if (counter == null)
            {
                return 0;
            }

            try
            {
                return counter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        private static void Enqueue(Queue<double> queue, double value)
        {
            queue.Enqueue(value);
            while (queue.Count > HistoryCapacity)
            {
                queue.Dequeue();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
