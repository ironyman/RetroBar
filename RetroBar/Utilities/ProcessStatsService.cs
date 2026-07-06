using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Timers;

namespace RetroBar.Utilities
{
    public enum ProcessMetric
    {
        Cpu,
        Memory,
        Disk,
        Network
    }

    public sealed class ProcessUsage
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
        public string Display { get; set; }
    }

    /// <summary>
    /// Samples per-process CPU, working set and disk I/O once per second so the tray
    /// gauges can offer a "top offenders" right-click menu. Network is ranked on demand
    /// by active TCP connection count (per-process throughput would require an elevated
    /// ETW session, which RetroBar doesn't have running asInvoker).
    /// </summary>
    public sealed class ProcessStatsService
    {
        private static readonly Lazy<ProcessStatsService> _lazy = new Lazy<ProcessStatsService>(() => new ProcessStatsService());
        public static ProcessStatsService Instance => _lazy.Value;

        private sealed class Sample
        {
            public string Name;
            public TimeSpan CpuTime;
            public long WorkingSet;
            public ulong IoBytes;
        }

        private readonly Timer _timer;
        private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
        private Dictionary<int, Sample> _previous;
        private DateTime _previousTime;

        // Latest ranked snapshots. Reference assignment is atomic; readers get a stable list.
        private volatile List<ProcessUsage> _cpu = new List<ProcessUsage>();
        private volatile List<ProcessUsage> _memory = new List<ProcessUsage>();
        private volatile List<ProcessUsage> _disk = new List<ProcessUsage>();

        private ProcessStatsService()
        {
            _previous = Snapshot();
            _previousTime = DateTime.UtcNow;

            _timer = new Timer(1000) { AutoReset = true };
            _timer.Elapsed += Timer_Elapsed;
            _timer.Start();
        }

        /// <summary>Ensures the background sampler is running.</summary>
        public void EnsureStarted()
        {
            // Touching Instance starts the timer; this is a no-op hook for callers.
        }

        public IReadOnlyList<ProcessUsage> GetTop(ProcessMetric metric, int count)
        {
            switch (metric)
            {
                case ProcessMetric.Cpu:
                    return _cpu.Take(count).ToList();
                case ProcessMetric.Memory:
                    return _memory.Take(count).ToList();
                case ProcessMetric.Disk:
                    return _disk.Take(count).ToList();
                case ProcessMetric.Network:
                    return GetTopNetwork(count);
                default:
                    return Array.Empty<ProcessUsage>();
            }
        }

        public bool TryKill(int pid, out string error)
        {
            error = null;
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                Dictionary<int, Sample> current = Snapshot();
                DateTime now = DateTime.UtcNow;
                double elapsedSeconds = Math.Max(0.001, (now - _previousTime).TotalSeconds);

                var cpu = new List<ProcessUsage>();
                var memory = new List<ProcessUsage>();
                var disk = new List<ProcessUsage>();

                foreach (KeyValuePair<int, Sample> kvp in current)
                {
                    Sample cur = kvp.Value;

                    memory.Add(new ProcessUsage
                    {
                        Pid = kvp.Key,
                        Name = cur.Name,
                        Value = cur.WorkingSet,
                        Display = FormatBytes((ulong)cur.WorkingSet)
                    });

                    if (_previous.TryGetValue(kvp.Key, out Sample prev))
                    {
                        double cpuPercent = (cur.CpuTime - prev.CpuTime).TotalSeconds / elapsedSeconds / _processorCount * 100.0;
                        if (cpuPercent > 0.1)
                        {
                            cpu.Add(new ProcessUsage
                            {
                                Pid = kvp.Key,
                                Name = cur.Name,
                                Value = cpuPercent,
                                Display = $"{cpuPercent:0.0}%"
                            });
                        }

                        double diskBps = cur.IoBytes >= prev.IoBytes ? (cur.IoBytes - prev.IoBytes) / elapsedSeconds : 0;
                        if (diskBps > 1024)
                        {
                            disk.Add(new ProcessUsage
                            {
                                Pid = kvp.Key,
                                Name = cur.Name,
                                Value = diskBps,
                                Display = $"{FormatBytes((ulong)diskBps)}/s"
                            });
                        }
                    }
                }

                _cpu = cpu.OrderByDescending(u => u.Value).ToList();
                _memory = memory.OrderByDescending(u => u.Value).ToList();
                _disk = disk.OrderByDescending(u => u.Value).ToList();

                _previous = current;
                _previousTime = now;
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("ProcessStatsService: sampling failed", ex);
            }
        }

        private Dictionary<int, Sample> Snapshot()
        {
            var result = new Dictionary<int, Sample>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    var sample = new Sample
                    {
                        Name = p.ProcessName,
                        WorkingSet = p.WorkingSet64,
                        CpuTime = p.TotalProcessorTime,
                        IoBytes = GetIoBytes(p)
                    };
                    result[p.Id] = sample;
                }
                catch
                {
                    // Protected/exited processes (Idle, System, secure processes) — skip.
                }
                finally
                {
                    p.Dispose();
                }
            }
            return result;
        }

        private static ulong GetIoBytes(Process p)
        {
            try
            {
                if (GetProcessIoCounters(p.Handle, out IO_COUNTERS counters))
                {
                    return counters.ReadTransferCount + counters.WriteTransferCount;
                }
            }
            catch
            {
            }
            return 0;
        }

        private static List<ProcessUsage> GetTopNetwork(int count)
        {
            var counts = new Dictionary<int, int>();
            try
            {
                foreach (int pid in EnumerateTcpOwnerPids())
                {
                    counts.TryGetValue(pid, out int c);
                    counts[pid] = c + 1;
                }
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("ProcessStatsService: TCP table read failed", ex);
                return new List<ProcessUsage>();
            }

            return counts
                .Where(kvp => kvp.Key > 0)
                .OrderByDescending(kvp => kvp.Value)
                .Take(count)
                .Select(kvp => new ProcessUsage
                {
                    Pid = kvp.Key,
                    Name = SafeProcessName(kvp.Key),
                    Value = kvp.Value,
                    Display = kvp.Value == 1 ? "1 connection" : $"{kvp.Value} connections"
                })
                .Where(u => u.Name != null)
                .ToList();
        }

        private static string SafeProcessName(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024UL * 1024) return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024UL * 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";
        }

        #region Native

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

        private static IEnumerable<int> EnumerateTcpOwnerPids()
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                {
                    yield break;
                }

                int rowCount = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = buffer + 4;
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                for (int i = 0; i < rowCount; i++)
                {
                    MIB_TCPROW_OWNER_PID row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    yield return (int)row.owningPid;
                    rowPtr += rowSize;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        #endregion
    }
}
