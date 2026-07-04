using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public Queue<double> CpuHistory { get; } = new Queue<double>(HistoryCapacity);
        public Queue<double> MemoryHistory { get; } = new Queue<double>(HistoryCapacity);
        public Queue<double> DiskHistory { get; } = new Queue<double>(HistoryCapacity);

        private readonly Timer _timer;
        private readonly Dispatcher _dispatcher;
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _diskCounter;

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

            _dispatcher.Invoke(() =>
            {
                CpuPercent = cpu;
                MemoryPercent = memPercent;
                DiskPercent = disk;
                MemoryUsedBytes = used;
                MemoryTotalBytes = total;

                Enqueue(CpuHistory, cpu);
                Enqueue(MemoryHistory, memPercent);
                Enqueue(DiskHistory, disk);

                StatsUpdated?.Invoke();
            });
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
