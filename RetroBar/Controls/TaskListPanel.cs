using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RetroBar.Controls
{
    // Lays task buttons out like a WrapPanel, but sizes them itself (from ButtonWidth, or a
    // button's own LayoutWidth while it slides in/out) instead of relying on each button's Width,
    // and snaps each button's *edges* to device pixels from their exact running positions.
    //
    // With a plain WrapPanel every button carried the same fractional mid-animation width, which
    // layout rounding rounded per button - so the row's trailing edge sat at count * rounded
    // width, jumping by several pixels whenever the eased width crossed a pixel boundary while
    // anything sized independently (a closing button's gap, a sliding button) moved a pixel at a
    // time. The last button visibly shook back and forth, and the rounded total could exceed the
    // row and wrap it onto a hidden second line for a frame. Flooring each edge's exact position
    // instead keeps every edge moving monotonically and the total within the row.
    public class TaskListPanel : WrapPanel
    {
        private const double FitEpsilon = 0.001;

        public static readonly DependencyProperty ButtonWidthProperty = DependencyProperty.Register(
            nameof(ButtonWidth), typeof(double), typeof(TaskListPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        // Empty space held open before a child, along the layout direction - used to keep a
        // closed button's slot open while it shrinks away (see TaskList.AnimateClosedButtonOut).
        public static readonly DependencyProperty LeadingGapProperty = DependencyProperty.RegisterAttached(
            "LeadingGap", typeof(double), typeof(TaskListPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static double GetLeadingGap(UIElement element) => (double)element.GetValue(LeadingGapProperty);
        public static void SetLeadingGap(UIElement element, double value) => element.SetValue(LeadingGapProperty, value);

        private struct Line
        {
            public int Start;
            public int End; // exclusive
            public double Thickness;
        }

        // A child's extent along the button-width axis: its button's LayoutWidth while that's
        // being animated, otherwise the shared ButtonWidth.
        private double SlotWidth(UIElement child)
        {
            if (child is ContentPresenter cp && TaskGroupManager.GetTaskButton(cp) is TaskButton btn && !double.IsNaN(btn.LayoutWidth))
                return Math.Max(0, btn.LayoutWidth);
            return Math.Max(0, ButtonWidth);
        }

        private bool IsHorizontal => Orientation == Orientation.Horizontal;

        // Extent along the line direction: the slot width for a horizontal taskbar, the button's
        // own height for a vertical one (where every button spans the full ButtonWidth).
        private double MainExtent(UIElement child) => IsHorizontal
            ? GetLeadingGap(child) + SlotWidth(child)
            : GetLeadingGap(child) + child.DesiredSize.Height;

        private double CrossExtent(UIElement child) => IsHorizontal ? child.DesiredSize.Height : SlotWidth(child);

        private List<Line> ComputeLines(double limit)
        {
            var lines = new List<Line>();
            var line = new Line();
            double pos = 0;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                if (child == null) continue;

                double extent = MainExtent(child);
                if (pos > 0 && pos + extent > limit + FitEpsilon)
                {
                    line.End = i;
                    lines.Add(line);
                    line = new Line { Start = i };
                    pos = 0;
                }

                pos += extent;
                line.Thickness = Math.Max(line.Thickness, CrossExtent(child));
            }

            line.End = InternalChildren.Count;
            lines.Add(line);
            return lines;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            bool horizontal = IsHorizontal;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;

                // Measuring applies the container's template, which is when a new button decides
                // whether to slide in (TaskButton.OnVisualParentChanged) - so re-measure if that
                // changed its slot width.
                double width = SlotWidth(child);
                child.Measure(horizontal ? new Size(width, constraint.Height) : new Size(width, double.PositiveInfinity));
                double settled = SlotWidth(child);
                if (settled != width)
                    child.Measure(horizontal ? new Size(settled, constraint.Height) : new Size(settled, double.PositiveInfinity));
            }

            double limit = horizontal ? constraint.Width : constraint.Height;
            var lines = ComputeLines(limit);

            double mainMax = 0, crossTotal = 0;
            foreach (var line in lines)
            {
                double pos = 0;
                for (int i = line.Start; i < line.End; i++)
                {
                    if (InternalChildren[i] != null)
                        pos += MainExtent(InternalChildren[i]);
                }
                mainMax = Math.Max(mainMax, pos);
                crossTotal += line.Thickness;
            }

            // Never report more than the constraint along the line direction - a lone child wider
            // than the row would otherwise make the panel request extra space.
            if (!double.IsInfinity(limit)) mainMax = Math.Min(mainMax, limit);

            return horizontal ? new Size(mainMax, crossTotal) : new Size(crossTotal, mainMax);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool horizontal = IsHorizontal;
            var dpi = VisualTreeHelper.GetDpi(this);
            double mainScale = horizontal ? dpi.DpiScaleX : dpi.DpiScaleY;
            double crossScale = horizontal ? dpi.DpiScaleY : dpi.DpiScaleX;

            var lines = ComputeLines(horizontal ? finalSize.Width : finalSize.Height);
            double crossPos = 0;

            foreach (var line in lines)
            {
                double crossStart = Snap(crossPos, crossScale);
                double crossEnd = Snap(crossPos + line.Thickness, crossScale);
                double pos = 0;

                for (int i = line.Start; i < line.End; i++)
                {
                    var child = InternalChildren[i];
                    if (child == null) continue;

                    pos += GetLeadingGap(child);
                    double start = Snap(pos, mainScale);
                    pos += horizontal ? SlotWidth(child) : child.DesiredSize.Height;
                    double end = Snap(pos, mainScale);

                    child.Arrange(horizontal
                        ? new Rect(start, crossStart, end - start, crossEnd - crossStart)
                        : new Rect(crossStart, start, crossEnd - crossStart, end - start));
                }

                crossPos += line.Thickness;
            }

            return finalSize;
        }

        // Floors to the device-pixel grid. The epsilon keeps a position that's exactly on a pixel
        // boundary (e.g. every edge once animations settle) from flooring a whole pixel short due
        // to floating-point error.
        private static double Snap(double value, double scale)
        {
            if (scale <= 0) scale = 1;
            return Math.Floor(value * scale + 1e-6) / scale;
        }
    }
}
