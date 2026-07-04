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
    /// <summary>
    /// Network tray gauge: two half-ring arcs split by small gaps at the top and bottom
    /// of the circle. The left half tracks download, the right half tracks upload; each
    /// fills from its bottom anchor upward as its share of the session's peak throughput rises.
    /// </summary>
    public partial class NetworkMeter : UserControl
    {
        private const string NetworkGlyph = "";

        // Gaps are centered on the top (90deg) and bottom (270deg) of the circle, splitting
        // the ring into a left half (download) and a right half (upload).
        private const double GapHalfAngle = 8;
        private const double DownloadStartAngle = 270 - GapHalfAngle; // bottom-left anchor
        private const double UploadStartAngle = 270 + GapHalfAngle;   // bottom-right anchor
        private const double TotalSweep = 180 - (2 * GapHalfAngle);
        private const double Radius = 6.5;
        private const double Center = 8;
        private const double RedThreshold = 90;

        public NetworkMeter()
        {
            InitializeComponent();
            Loaded += NetworkMeter_OnLoaded;
            Unloaded += NetworkMeter_OnUnloaded;
        }

        private void NetworkMeter_OnLoaded(object sender, RoutedEventArgs e)
        {
            TipTitle.Text = (string)Application.Current.FindResource("system_stats_network");
            SystemStatsService.Instance.StatsUpdated += SystemStatsService_OnStatsUpdated;
            Refresh();
        }

        private void NetworkMeter_OnUnloaded(object sender, RoutedEventArgs e)
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

            double downPercent = Math.Max(0, Math.Min(100, service.DownloadPercent));
            double upPercent = Math.Max(0, Math.Min(100, service.UploadPercent));

            DownloadTrackPath.Data = BuildArc(DownloadStartAngle, TotalSweep, clockwise: true);
            UploadTrackPath.Data = BuildArc(UploadStartAngle, TotalSweep, clockwise: false);

            double downSweep = downPercent / 100.0 * TotalSweep;
            double upSweep = upPercent / 100.0 * TotalSweep;

            DownloadFillPath.Data = BuildArc(DownloadStartAngle, downSweep, clockwise: true);
            DownloadFillPath.Stroke = downPercent >= RedThreshold ? Brushes.Red : (Brush)FindResource("ClockForeground");

            UploadFillPath.Data = BuildArc(UploadStartAngle, upSweep, clockwise: false);
            UploadFillPath.Stroke = upPercent >= RedThreshold ? Brushes.Red : (Brush)FindResource("ClockForeground");

            if (IconText.Text != NetworkGlyph)
            {
                IconText.Text = NetworkGlyph;
            }

            TipDownloadDetail.Text = $"↓ {FormatSpeed(service.DownloadBytesPerSecond)}";
            TipUploadDetail.Text = $"↑ {FormatSpeed(service.UploadBytesPerSecond)}";

            RedrawGraph(DownloadGraphCanvas, service.DownloadHistory, downPercent >= RedThreshold);
            RedrawGraph(UploadGraphCanvas, service.UploadHistory, upPercent >= RedThreshold);
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024)
            {
                return $"{bytesPerSecond:0} B/s";
            }

            if (bytesPerSecond < 1024 * 1024)
            {
                return $"{bytesPerSecond / 1024:0.0} KB/s";
            }

            return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        }

        private static Geometry BuildArc(double startAngleDeg, double sweepDeg, bool clockwise)
        {
            sweepDeg = Math.Max(0, Math.Min(359.999, sweepDeg));
            if (sweepDeg <= 0)
            {
                return Geometry.Empty;
            }

            double endAngleDeg = clockwise ? startAngleDeg - sweepDeg : startAngleDeg + sweepDeg;
            Point start = PointOnCircle(startAngleDeg);
            Point end = PointOnCircle(endAngleDeg);
            bool isLargeArc = sweepDeg > 180.0;

            PathFigure figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, isLargeArc,
                clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static Point PointOnCircle(double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(Center + (Radius * Math.Cos(rad)), Center - (Radius * Math.Sin(rad)));
        }

        private void RedrawGraph(Canvas canvas, IEnumerable<double> history, bool isHot)
        {
            List<double> points = history.ToList();
            canvas.Children.Clear();
            if (points.Count < 2)
            {
                return;
            }

            double w = canvas.Width;
            double h = canvas.Height;
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

            canvas.Children.Add(polyline);
        }
    }
}
