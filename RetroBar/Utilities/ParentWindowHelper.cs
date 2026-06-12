using ManagedShell.WindowsTasks;
using System.Collections.Generic;

namespace RetroBar.Utilities
{
    internal static class ParentWindowHelper
    {
        internal static int FindInsertionIndex(ApplicationWindow _, IList<ApplicationWindow> windows)
        {
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                if (windows[i].State == ApplicationWindow.WindowState.Active)
                    return i + 1;
            }
            return -1;
        }
    }
}
