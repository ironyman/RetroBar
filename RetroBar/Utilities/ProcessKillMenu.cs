using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Populates a <see cref="ContextMenu"/> with the top resource-consuming processes for a
    /// given metric; clicking an entry ends that process.
    /// </summary>
    internal static class ProcessKillMenu
    {
        private const int TopCount = 5;

        public static void Populate(ContextMenu menu, ProcessMetric metric)
        {
            menu.Items.Clear();

            IReadOnlyList<ProcessUsage> top = ProcessStatsService.Instance.GetTop(metric, TopCount);

            menu.Items.Add(new MenuItem { Header = HeaderFor(metric), IsEnabled = false });
            menu.Items.Add(new Separator());

            if (top.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "(no data)", IsEnabled = false });
                return;
            }

            foreach (ProcessUsage usage in top)
            {
                int pid = usage.Pid;
                string name = usage.Name;
                MenuItem item = new MenuItem { Header = $"{name}  —  {usage.Display}" };
                item.Click += (s, e) =>
                {
                    if (!ProcessStatsService.Instance.TryKill(pid, out string error))
                    {
                        MessageBox.Show(
                            $"Couldn't end {name} (PID {pid}).\n{error}",
                            "RetroBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                menu.Items.Add(item);
            }
        }

        private static string HeaderFor(ProcessMetric metric)
        {
            switch (metric)
            {
                case ProcessMetric.Cpu: return "Top CPU — click to end";
                case ProcessMetric.Memory: return "Top memory — click to end";
                case ProcessMetric.Disk: return "Top disk I/O — click to end";
                case ProcessMetric.Network: return "Most TCP connections — click to end";
                default: return "Click to end";
            }
        }
    }
}
