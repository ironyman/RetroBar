using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    public enum ResourceMeterKind
    {
        Cpu,
        Memory,
        Disk
    }

    /// <summary>
    /// A single tray gauge: an icon ringed by an arc that fills clockwise-from-bottom-right
    /// as the underlying stat rises, with a gap left open at the bottom (~270 degrees).
    /// </summary>
    public partial class ResourceMeter : UserControl
    {
        // https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font
        private const string CpuGlyph = "\uEEA1";   // CPU
        private const string MemoryGlyph = "\uEEA0"; // RAM
        private const string DiskGlyph = "\uEE94"; // Wheel

        // The ring has a gap centered on the bottom (270 deg, standard math convention: 0=right, 90=up).
        // GapHalfAngle controls how wide that gap is; the track spans the remaining 360-2*GapHalfAngle degrees,
        // starting at the bottom-right end and sweeping counter-clockwise to the bottom-left end.
        private const double GapHalfAngle = 35;
        private const double StartAngle = 270 + GapHalfAngle;
        private const double TotalSweep = 360 - (2 * GapHalfAngle);
        private const double Radius = 8.2;
        private const double Center = 10;
        private const double RedThreshold = 90;

        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(ResourceMeterKind), typeof(ResourceMeter),
            new PropertyMetadata(ResourceMeterKind.Cpu, OnKindChanged));

        public ResourceMeterKind Kind
        {
            get => (ResourceMeterKind)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public ResourceMeter()
        {
            InitializeComponent();
            Loaded += ResourceMeter_OnLoaded;
            Unloaded += ResourceMeter_OnUnloaded;
        }

        private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResourceMeter meter)
            {
                meter.ApplyKind();
                meter.Refresh();
            }
        }

        private void ApplyKind()
        {
            switch (Kind)
            {
                case ResourceMeterKind.Cpu:
                    IconText.Text = CpuGlyph;
                    TipTitle.Text = (string)Application.Current.FindResource("system_stats_cpu");
                    break;
                case ResourceMeterKind.Memory:
                    IconText.Text = MemoryGlyph;
                    TipTitle.Text = (string)Application.Current.FindResource("system_stats_memory");
                    break;
                case ResourceMeterKind.Disk:
                    IconText.Text = DiskGlyph;
                    TipTitle.Text = (string)Application.Current.FindResource("system_stats_disk");
                    break;
            }
        }

        private void ResourceMeter_OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyKind();
            SystemStatsService.Instance.StatsUpdated += SystemStatsService_OnStatsUpdated;
            Refresh();
        }

        private void ResourceMeter_OnUnloaded(object sender, RoutedEventArgs e)
        {
            SystemStatsService.Instance.StatsUpdated -= SystemStatsService_OnStatsUpdated;
        }

        private void SystemStatsService_OnStatsUpdated()
        {
            Refresh();
        }

        private void Refresh()
        {
            var service = SystemStatsService.Instance;
            double value;
            IEnumerable<double> history;
            string detail;

            switch (Kind)
            {
                case ResourceMeterKind.Memory:
                    value = service.MemoryPercent;
                    history = service.MemoryHistory;
                    detail = $"{value:0}% ({FormatBytes(service.MemoryUsedBytes)} / {FormatBytes(service.MemoryTotalBytes)})";
                    break;
                case ResourceMeterKind.Disk:
                    value = service.DiskPercent;
                    history = service.DiskHistory;
                    detail = $"{value:0}%";
                    break;
                default:
                    value = service.CpuPercent;
                    history = service.CpuHistory;
                    detail = $"{value:0}%";
                    break;
            }

            if (TipDetail != null)
            {
                TipDetail.Text = detail;
            }

            UpdateRing(value);
            RedrawGraph(history, value >= RedThreshold);
        }

        private static string FormatBytes(ulong bytes)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
        }

        private void UpdateRing(double value)
        {
            value = Math.Max(0, Math.Min(100, value));
            double sweep = value / 100.0 * TotalSweep;
            Brush activeBrush = value >= RedThreshold ? Brushes.Red : (Brush)FindResource("ClockForeground");

            TrackPath.Data = BuildArc(TotalSweep);
            FillPath.Data = BuildArc(sweep);
            FillPath.Stroke = activeBrush;
        }

        private static Geometry BuildArc(double sweepDeg)
        {
            sweepDeg = Math.Max(0, Math.Min(359.999, sweepDeg));
            if (sweepDeg <= 0)
            {
                return Geometry.Empty;
            }

            Point start = PointOnCircle(StartAngle);
            Point end = PointOnCircle(StartAngle + sweepDeg);
            bool isLargeArc = sweepDeg > 180.0;

            PathFigure figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, isLargeArc, SweepDirection.Counterclockwise, true));

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static Point PointOnCircle(double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(Center + (Radius * Math.Cos(rad)), Center - (Radius * Math.Sin(rad)));
        }

        private void RedrawGraph(IEnumerable<double> history, bool isHot)
        {
            if (GraphCanvas == null)
            {
                return;
            }

            List<double> points = history.ToList();
            GraphCanvas.Children.Clear();
            if (points.Count < 2)
            {
                return;
            }

            double w = GraphCanvas.Width;
            double h = GraphCanvas.Height;
            double step = w / (SystemStatsService.HistoryCapacity - 1);

            Polyline polyline = new Polyline
            {
                Stroke = isHot ? Brushes.Red : (Brush)FindResource("ClockForeground"),
                StrokeThickness = 1
            };

            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                double x = w - ((n - 1 - i) * step);
                double y = h - (points[i] / 100.0 * h);
                polyline.Points.Add(new Point(x, y));
            }

            GraphCanvas.Children.Add(polyline);
        }
    }
}
