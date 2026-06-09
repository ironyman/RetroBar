using System.Collections;
using ManagedShell.WindowsTray;

namespace RetroBar.Utilities
{
    internal class NotifyIconOrderComparer : IComparer
    {
        private readonly IList _source;

        public NotifyIconOrderComparer(IList source)
        {
            _source = source;
        }

        public int Compare(object x, object y)
        {
            var order = Settings.Instance.NotifyIconOrder;
            int ia = x is NotifyIcon a ? order.IndexOf(a.Identifier) : -1;
            int ib = y is NotifyIcon b ? order.IndexOf(b.Identifier) : -1;

            if (ia >= 0 && ib >= 0) return ia.CompareTo(ib);
            if (ia >= 0) return -1;
            if (ib >= 0) return 1;

            // Both not in order list: preserve original TrayIcons insertion order
            return _source.IndexOf(x).CompareTo(_source.IndexOf(y));
        }
    }
}
