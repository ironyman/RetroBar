Implemented and compiles cleanly — the only build failure is again the deploy-copy step: RetroBar (PID 27788) is running and locking bin\...\RetroBar.exe, so the new build is stranded in obj\ and not deployed yet.

The new rule in UpdateButtonDrag (TaskList.xaml.cs):

The dragged block's home (the "hole") and the neighboring block are always adjacent in display order, so together they form one combined span. A swap fires when the dragged block's midpoint (full span of all moving buttons, plus cursor delta) crosses that combined span's midpoint.
Works for any dragged-group size against any target-group size — both sides are measured as whole spans from the visual slots they currently occupy.
Validation of no hysteresis / no jitter, as you asked:

Swap-invariant threshold. The pair occupies the same total extent before and after swapping ([h, h+Wd+Wt) either way), so the combined midpoint is identical in both directions. Forward and reverse thresholds are the exact same line — zero threshold band, which is the definition of no hysteresis.
No back-and-forth fighting. A swap only ever fires by crossing that line, and the line is computed from the static slot snapshot taken at drag start — never from animated/intermediate positions — so a swap cannot move its own trigger and re-fire itself. I also verified the in-loop case: immediately after a forward step, the reverse check evaluates the same pair over the same slot range with a strict inequality, which is false by construction.
No double-fires across neighbors. Adjacent pairs' thresholds are always separated by (W₁+W₂)/2 > 0, so consecutive boundaries can't collide even with tiny buttons.
The trigger uses strict > / <, so sitting exactly on the line does nothing.

Fix new window button inserts

sometimes when i open a window when there are button groups in task bar, it doesn't show up, or it shows up but splits the button group i'm in, it shouldn't split the button group, just insert it after the whole button group

Splitting a group on insert: The old check only looked at prevWindow (the item immediately before the new one) to find a group to protect. If the insertion landed inside a group's span for any other reason, it went undetected. Replaced with FixupGroupSplitInsertions, which checks every new window against every existing group's actual index span (min/max member index) and relocates it to right after the group whenever it lands strictly inside — this covers the general case, not just the one heuristic path.

Window not showing up at all: The real suspect here — the old code called source.Move(...) synchronously, from inside the same CollectionChanged callback that WPF's ItemsControl is also subscribed to for that same Add notification. Mutating the collection again before WPF finishes generating the container for the newly added item is a known way to end up with a container that never gets created — which would explain a button intermittently just not appearing. Fixed by deferring the fixup via Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...), so it runs only after WPF has fully processed the Add.

As always: exit RetroBar → dotnet build (confirm 0 errors) → relaunch to actually test this, since the running instance is still on stale code.

