# Tray Icon Data Model

## Core Class: `NotifyIcon` (ManagedShell)

Each icon is identified by one of:
- `Guid GUID` (preferred, newer icons)
- `Path:UID:Title` string (fallback)

The computed `Identifier` property unifies these into a single string key used everywhere in settings.

Key properties:
- `IntPtr HWnd` — window handle of the icon owner
- `uint UID` — unique ID within the window
- `ImageSource Icon` — the visual icon image
- `string Title` — tooltip text
- `string Path` — path to the owning application
- `bool IsPinned` — whether the icon is pinned (visible by default)
- `bool IsHidden` — whether the icon is hidden from display
- `int PinOrder` — order index in the pinned list

---

## Visibility / Behavior

Stored in `Settings.NotifyIconBehaviors` as a `List<NotifyIconBehaviorSetting>`:

```csharp
enum NotifyIconBehavior { HideWhenInactive, AlwaysHide, AlwaysShow, Remove }
```

| Value | Meaning |
|---|---|
| `AlwaysShow` | Pinned — visible in the tray by default |
| `HideWhenInactive` | Shown temporarily when a notification arrives |
| `AlwaysHide` | Never shown |
| `Remove` | Hidden and not tracked |

`NotifyIcon.IsHidden` and `IsPinned` are derived live from this setting.
`GetBehavior()` / `SetBehavior()` in `RetroBar/Extensions/NotifyIconExtensions.cs` read/write it.

---

## Position / Order

Stored in `Settings.NotifyIconOrder` as a `List<string>` of identifiers.

- A sentinel string `"__tray_separator__"` sits in the list — icons **before** it are hidden by default, icons **after** are shown
- `RetroBar/Utilities/NotifyIconOrderComparer.cs` sorts the live collection by position in this list
- `RetroBar/Utilities/SeparatorPlaceholder.cs` is the draggable UI object for the separator

---

## Persistence

Everything lives in `%LOCALAPPDATA%\RetroBar\settings.json`, written by `SettingsManager<Settings>` using `System.Text.Json`. Key tray-related fields:

| Field | Type | Purpose |
|---|---|---|
| `NotifyIconOrder` | `List<string>` | Display order + separator position |
| `NotifyIconBehaviors` | `List<NotifyIconBehaviorSetting>` | Per-icon visibility rule |
| `InvertNotifyIcons` | `List<string>` | Icons with color inversion |
| `CollapseNotifyIcons` | `bool` | Show only pinned by default |

Default pinned system icons: Volume, Power, Network, Health (`GUID`s in `NotificationArea.cs`).

---

## Display Pipeline

`NotifyIconList` maintains several filtered views over the live `NotifyIcon` collection:

| View | Contents |
|---|---|
| `_allUserIcons` | All non-system icons |
| `_pinnedUserIcons` | Filtered to `IsPinned == true` |
| `promotedIcons` | Temporarily shown `HideWhenInactive` icons |
| `_displayItems` | Composite shown in UI: promoted + separator + pinned |

Drag-and-drop reordering updates `Settings.NotifyIconOrder` directly.

---

## Data Flow

1. **Icon creation** — `NotificationArea` detects via Win32 `Shell_NotifyIcon` API
2. **Identification** — assigned `Identifier` (GUID or Path:UID:Title)
3. **Behavior assignment** — fetched from `Settings.NotifyIconBehaviors`, defaults to `HideWhenInactive`
4. **Order assignment** — sorted by `NotifyIconOrderComparer` using `Settings.NotifyIconOrder`
5. **Display filtering** — applied by filters in `NotifyIconList`
6. **User interaction** — drag/drop updates order and behaviors
7. **Persistence** — `SettingsManager` auto-saves `settings.json` on property changes
