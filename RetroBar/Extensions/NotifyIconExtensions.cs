using ManagedShell.WindowsTray;
using RetroBar.Utilities;
using System.Collections.Generic;

namespace RetroBar.Extensions
{
    public static class NotifyIconExtensions
    {
        internal static readonly HashSet<string> SystemIconGuids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            NotificationArea.HEALTH_GUID,
            NotificationArea.POWER_GUID,
            NotificationArea.VOLUME_GUID,
            NetworkTrayIcon.GUID_STRING
        };

        public static bool IsSystemIcon(this NotifyIcon icon)
        {
            return SystemIconGuids.Contains(icon.GUID.ToString());
        }

        // A stable identifier for persistence. Unlike NotifyIcon.Identifier, this omits the
        // icon's Title, which some apps (e.g. Steam showing download progress) mutate constantly.
        // Using the title in stored order/behavior lists makes entries go stale the moment the
        // tooltip changes, so we key off GUID or Path:UID instead — matching how ManagedShell
        // matches pinned icons in IsEqualByIdentifier.
        public static string GetStableIdentifier(this NotifyIcon icon)
        {
            if (icon.GUID != default) return icon.GUID.ToString();
            else return icon.Path + ":" + icon.UID.ToString();
        }

        // Reduces a stored (possibly title-bearing) identifier to its stable form so legacy
        // settings can be migrated. GUID strings have no ':' and pass through unchanged; path
        // identifiers are "drive:path:uid[:title]" — keep the first three segments.
        public static string ToStableIdentifier(string storedIdentifier)
        {
            if (string.IsNullOrEmpty(storedIdentifier)) return storedIdentifier;
            string[] parts = storedIdentifier.Split(new[] { ':' }, 4);
            if (parts.Length < 3) return storedIdentifier;
            return parts[0] + ":" + parts[1] + ":" + parts[2];
        }

        public static string GetInvertIdentifier(this NotifyIcon icon)
        {
            return icon.GetStableIdentifier();
        }

        public static NotifyIconBehavior GetBehavior(this NotifyIcon icon)
        {
            if (icon.IsPinned)
            {
                return NotifyIconBehavior.AlwaysShow;
            }

            string identifier = icon.GetStableIdentifier();
            if (Settings.Instance.NotifyIconBehaviors.Find(setting => setting.Identifier == identifier) is NotifyIconBehaviorSetting iconSetting)
            {
                return iconSetting.Behavior;
            }

            return NotifyIconBehavior.HideWhenInactive;
        }

        public static void SetBehavior(this NotifyIcon icon, NotifyIconBehavior behavior)
        {
            string identifier = icon.GetStableIdentifier();
            var settings = new List<NotifyIconBehaviorSetting>(Settings.Instance.NotifyIconBehaviors);
            var currentSettingIndex = settings.FindIndex(setting => setting.Identifier == identifier);

            if (currentSettingIndex >= 0)
            {
                if (behavior == NotifyIconBehavior.HideWhenInactive)
                {
                    // Switching back to default value; remove from settings
                    settings.RemoveAt(currentSettingIndex);
                }
                else
                {
                    settings[currentSettingIndex] = new NotifyIconBehaviorSetting
                    {
                        Identifier = identifier,
                        Behavior = behavior
                    };
                }
            }
            else
            {
                settings.Add(new NotifyIconBehaviorSetting
                {
                    Identifier = identifier,
                    Behavior = behavior
                });
            }

            Settings.Instance.NotifyIconBehaviors = settings;

            if (icon.IsPinned != (behavior == NotifyIconBehavior.AlwaysShow))
            {
                // Update pinned values in ManagedShell
                if (behavior == NotifyIconBehavior.AlwaysShow)
                {
                    icon.Pin();
                }
                else
                {
                    icon.Unpin();
                }
            }
            else
            {
                // Trigger a refresh of the collections
                icon.OnPropertyChanged("IsPinned");
            }

            // Keep the order lists consistent with the new behavior so the icon
            // appears on the correct side of the separator in both collapsed and
            // expanded views. This is a no-op when called from drag-commit code
            // because drag-commit writes the order lists before calling SetBehavior.
            SyncOrderListForBehavior(identifier, behavior);
        }

        private static void SyncOrderListForBehavior(string identifier, NotifyIconBehavior behavior)
        {
            var hideOrder = Settings.Instance.NotifyIconOrderHide;
            var pinnedOrder = Settings.Instance.NotifyIconOrderPinned;
            bool inHide = hideOrder.Contains(identifier);
            bool inPinned = pinnedOrder.Contains(identifier);

            if (behavior == NotifyIconBehavior.AlwaysShow)
            {
                if (!inPinned || inHide)
                {
                    var newHide = new List<string>(hideOrder);
                    var newPinned = new List<string>(pinnedOrder);
                    newHide.Remove(identifier);
                    if (!newPinned.Contains(identifier)) newPinned.Add(identifier);
                    Settings.Instance.NotifyIconOrderHide = newHide;
                    Settings.Instance.NotifyIconOrderPinned = newPinned;
                }
            }
            else if (behavior == NotifyIconBehavior.HideWhenInactive)
            {
                if (!inHide || inPinned)
                {
                    var newHide = new List<string>(hideOrder);
                    var newPinned = new List<string>(pinnedOrder);
                    newPinned.Remove(identifier);
                    if (!newHide.Contains(identifier)) newHide.Add(identifier);
                    Settings.Instance.NotifyIconOrderHide = newHide;
                    Settings.Instance.NotifyIconOrderPinned = newPinned;
                }
            }
            else // AlwaysHide or Remove — take it out of both lists
            {
                if (inHide || inPinned)
                {
                    var newHide = new List<string>(hideOrder);
                    var newPinned = new List<string>(pinnedOrder);
                    newHide.Remove(identifier);
                    newPinned.Remove(identifier);
                    Settings.Instance.NotifyIconOrderHide = newHide;
                    Settings.Instance.NotifyIconOrderPinned = newPinned;
                }
            }
        }

        public static bool CanInvert(this NotifyIcon icon) {
            return Settings.Instance.InvertNotifyIcons.Contains(icon.GetInvertIdentifier());
        }

        public static void SetCanInvert(this NotifyIcon icon, bool canInvert)
        {
            var identifier = icon.GetInvertIdentifier();
            var settings = new List<string>(Settings.Instance.InvertNotifyIcons);
            var changed = false;

            if (!canInvert)
            {
                changed = settings.Remove(identifier);
            }
            else if (!settings.Contains(identifier))
            {
                settings.Add(identifier);
                changed = true;
            }

            if (changed)
            {
                Settings.Instance.InvertNotifyIcons = settings;
            }
        }
    }
}
