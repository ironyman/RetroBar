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

        public static string GetInvertIdentifier(this NotifyIcon icon)
        {
            if (icon.GUID != default) return icon.GUID.ToString();
            else return icon.Path + ":" + icon.UID.ToString();
        }

        public static NotifyIconBehavior GetBehavior(this NotifyIcon icon)
        {
            if (icon.IsPinned)
            {
                return NotifyIconBehavior.AlwaysShow;
            }

            if (Settings.Instance.NotifyIconBehaviors.Find(setting => setting.Identifier == icon.Identifier) is NotifyIconBehaviorSetting iconSetting)
            {
                return iconSetting.Behavior;
            }

            return NotifyIconBehavior.HideWhenInactive;
        }

        public static void SetBehavior(this NotifyIcon icon, NotifyIconBehavior behavior)
        {
            var settings = new List<NotifyIconBehaviorSetting>(Settings.Instance.NotifyIconBehaviors);
            var currentSettingIndex = settings.FindIndex(setting => setting.Identifier == icon.Identifier);

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
                        Identifier = icon.Identifier,
                        Behavior = behavior
                    };
                }
            }
            else
            {
                settings.Add(new NotifyIconBehaviorSetting
                {
                    Identifier = icon.Identifier,
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
            SyncOrderListForBehavior(icon.Identifier, behavior);
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
