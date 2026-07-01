when dragging tray icons to move, sometimes they don't end up at the place i moved them to, can you investigate why

it's a race in the drag code you just added (commit d386262).

Root cause: mid-drag rebuilds silently cancel/misplace the drag.

In NotifyIconList.xaml.cs, StartIconDrag snapshots the current icon containers/positions once (_iconDragContainers, _iconDragSlots), and the whole reorder algorithm (UpdateIconDrag/CommitIconDrag) depends on that snapshot staying valid until you release the mouse.

But several unrelated events call RebuildDisplayItems (which does _displayItems.Clear() + re-Add) with no check for whether a drag is in progress:

AllUserIcons_CollectionChanged — fires whenever any tray icon's IsHidden/IsPinned flips (live filtering), or any icon is added/removed. This includes icons you're not dragging — e.g. Wi-Fi/volume/battery/Bluetooth icons flicker their hidden state in the background all the time.
Settings_PropertyChanged — same thing on settings changes.
Clear() on an ObservableCollection raises a Reset, which makes WPF's ItemsControl tear down and recreate every container. Since your drag has Mouse.Capture held on the specific NotifyIconBorder inside one of those now-destroyed containers, WPF automatically releases the capture — which fires NotifyIcon_OnLostMouseCapture, which calls EndIconDrag() immediately, ending the drag right then, wherever _iconDragToIndex happened to be — not necessarily where your cursor was, and not where you intended to drop.

That explains the "sometimes": it only happens when some other tray icon's visibility/pin state churns at the exact moment you're mid-drag — timing-dependent, so intermittent and hard to pin down by eye.

(Secondary, unrelated issue: the drag math picks its axis via Host.Orientation — the taskbar's screen-edge orientation — but the WrapPanel it's measuring actually flows per RowCount/IconListOrientation, so if you use a horizontal taskbar with more than 1 row, the reorder math uses the wrong axis entirely. Only matters if you have multi-row tray icons enabled.)

Want me to fix the mid-drag rebuild race (guard RebuildDisplayItems/collection-changed handlers while _isIconDragging is true, and flush the pending rebuild once the drag commits)?

The code compiles cleanly — all the build errors are just file-lock issues (MSB3027/MSB3021) from copying output DLLs, because your running RetroBar.exe (PID 7012) has them open. No CS compile errors at all.

Summary of the fix, in NotifyIconList.xaml.cs:

Mid-drag rebuild race — Settings_PropertyChanged and AllUserIcons_CollectionChanged now check _isIconDragging before refreshing/rebuilding the icon collections. If a drag is active, they set _pendingCollectionRefresh/_pendingDisplayItemsRebuild instead of tearing down containers immediately. CommitIconDrag flushes those pending updates once the drag has fully settled, via the new FlushPendingIconListUpdates().
Wrong axis with multi-row tray icons — UpdateIconDrag now reads the actual WrapPanel.Orientation (_iconDragPanel.Orientation) instead of Host.Orientation (the taskbar's screen-edge), so the reorder math uses the correct axis when RowCount > 1 on a horizontal taskbar.
To finish verifying, close the running RetroBar instance and rebuild, or just relaunch it from Visual Studio/F5 to pick up the fix. Let me know if you'd like me to close the running process and rebuild now.