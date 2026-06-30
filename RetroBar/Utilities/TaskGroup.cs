using System;
using System.Collections.Generic;
using System.Windows.Media;
using ManagedShell.WindowsTasks;

namespace RetroBar.Utilities
{
    public class TaskGroup
    {
        private static readonly Random _random = new Random();

        public Color GroupColor { get; }
        public List<ApplicationWindow> Windows { get; } = new List<ApplicationWindow>();

        public TaskGroup() : this(RandomColor()) { }

        public TaskGroup(Color color)
        {
            GroupColor = color;
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
