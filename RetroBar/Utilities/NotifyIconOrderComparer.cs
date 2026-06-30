using System;
using System.Collections;
using System.Collections.Generic;
using ManagedShell.WindowsTray;
using RetroBar.Extensions;

namespace RetroBar.Utilities
{
    internal class NotifyIconOrderComparer : IComparer
    {
        private readonly IList _source;
        private readonly Func<List<string>> _getOrder;

        public NotifyIconOrderComparer(IList source, Func<List<string>> getOrder)
        {
            _source = source;
            _getOrder = getOrder;
        }

        public int Compare(object x, object y)
        {
            var order = _getOrder();
            int ia = x is NotifyIcon a ? order.IndexOf(a.GetStableIdentifier()) : -1;
            int ib = y is NotifyIcon b ? order.IndexOf(b.GetStableIdentifier()) : -1;

            if (ia >= 0 && ib >= 0) return ia.CompareTo(ib);
            if (ia >= 0) return -1;
            if (ib >= 0) return 1;

            // Both not in order list: preserve original TrayIcons insertion order
            return _source.IndexOf(x).CompareTo(_source.IndexOf(y));
        }
    }
}
