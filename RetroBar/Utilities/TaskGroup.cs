using System;
using System.Collections.Generic;
using System.Windows.Media;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;

namespace RetroBar.Utilities
{
    public class TaskGroup
    {
        private static readonly Random _random = new Random();

        public Color GroupColor { get; set; }
        public bool IsCollapsed { get; set; }

        // Membership is tracked by HWND, not by ApplicationWindow instance: a window hidden for a
        // workspace switch is treated as destroyed by the shell (its ApplicationWindow is disposed
        // and dropped from the collection) and comes back as a brand-new instance when re-shown.
        // The HWND is the only identity that survives that round trip, so holding instances here
        // would leave the group full of stale, dead objects after every workspace switch. Live
        // instances are resolved on demand via TaskGroupManager.
        public List<IntPtr> Handles { get; } = new List<IntPtr>();

        // Whether this group was tiled via Aero Snap. Cleared when any member window
        // moves away from its expected tile position.
        public bool IsTiled { get; set; }
        // Expected window rects after tiling (handle -> rect). Used to detect when a
        // window leaves its tile position.
        private Dictionary<IntPtr, NativeMethods.Rect> _tileRects;

        public TaskGroup() : this(RandomColor()) { }

        public TaskGroup(Color color)
        {
            GroupColor = color;
        }

        public void SetTiledRects(IReadOnlyList<ApplicationWindow> windows)
        {
            _tileRects = new Dictionary<IntPtr, NativeMethods.Rect>();
            NativeMethods.Rect rect = new NativeMethods.Rect();
            foreach (var w in windows)
            {
                if (NativeMethods.GetWindowRect(w.Handle, out rect))
                    _tileRects[w.Handle] = rect;
            }
            IsTiled = true;
        }

        // Checks whether ALL member windows are still at their expected tile positions.
        // Returns false if any window moved, or if there are no stored rects.
        public bool AreAllWindowsStillTiled()
        {
            if (_tileRects == null || _tileRects.Count == 0)
                return false;

            NativeMethods.Rect actual = new NativeMethods.Rect();
            foreach (var h in Handles)
            {
                if (!_tileRects.TryGetValue(h, out var expected))
                    return false;
                if (!NativeMethods.GetWindowRect(h, out actual))
                    return false;
                if (actual.Left != expected.Left || actual.Top != expected.Top
                    || actual.Right != expected.Right || actual.Bottom != expected.Bottom)
                    return false;
            }
            return true;
        }

        public void ClearTiled()
        {
            IsTiled = false;
            _tileRects = null;
        }

        public static Color RandomColor()
        {
            double hue = _random.NextDouble() * 360;
            return HsvToColor(hue, 0.80, 0.90);
        }

        private static Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            int hi = (int)(h / 60) % 6;
            double f = h / 60 - Math.Floor(h / 60);
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
