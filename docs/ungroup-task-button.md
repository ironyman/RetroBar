# Slide Animation When Removing a Window From a Group

When a window is removed from a task group ("Remove from group" on the button's
context menu → `RemoveFromGroupMenuItem_OnClick` in
[`TaskButton.xaml.cs`](../RetroBar/Controls/TaskButton.xaml.cs), which calls
`TaskList.UngroupWindow`), the ungrouped window is relocated in the taskbar next to
its old group (see `TaskGroupManager.UngroupWindow` in
[`TaskGroupManager.cs`](../RetroBar/Controls/TaskGroupManager.cs)). That relocation
shifts every button between the window's old and new slot over by one position.

Without any special handling, WPF just snaps every shifted button straight to its
new slot the instant the underlying collection changes — there's no built-in
"animate to new layout position" behavior for an `ItemsControl`. `UngroupWindow` in
[`TaskList.xaml.cs`](../RetroBar/Controls/TaskList.xaml.cs) makes that reflow slide
instead, using a technique called **FLIP**.

## What FLIP is

FLIP stands for **F**irst, **L**ast, **I**nvert, **P**lay. It's a general trick for
animating a layout change in any UI toolkit that doesn't support animating layout
directly (this includes the web's flexbox/grid *and* WPF's `ItemsControl`/`Panel`
layout — neither one interpolates a child's position when it moves in the layout,
only explicit property animations do). The idea:

1. **First** — record each element's position *before* the layout change.
2. **Last** — let the layout change happen (instantly, the normal way).
3. **Invert** — for each element, compute the delta between its First and Last
   position, and apply that delta as a transform *in reverse*. Visually, the
   element appears not to have moved at all — it's back at its First position, just
   achieved by sitting at the Last position with an offsetting transform on top.
4. **Play** — animate that transform down to zero. The element visibly slides from
   where it *was* to where it now *belongs*, even though the actual layout jumped
   there instantly and only the transform is being animated.

The advantage over animating layout properties directly (e.g. margins) is that
transforms are cheap, GPU-composited, and don't re-trigger layout passes on every
frame — the expensive layout pass happens exactly once (the instant jump), and the
animation on top of it is pure paint.

## How it's implemented here

`UngroupWindow` ([`TaskList.xaml.cs`](../RetroBar/Controls/TaskList.xaml.cs))
does the four FLIP steps like this:

**First** — `CaptureButtonPositions()` walks every currently-rendered
`TaskButton` container and records its position relative to the `WrapPanel`
(via `ContentPresenter.TranslatePoint`), keyed by the `ApplicationWindow`
each button represents (not by container instance — see below).

```csharp
private Dictionary<ApplicationWindow, Point> CaptureButtonPositions()
{
    var positions = new Dictionary<ApplicationWindow, Point>();
    var panel = FindItemsPanel<WrapPanel>(TasksList);
    ...
    positions[w] = cp.TranslatePoint(new Point(0, 0), panel);
    ...
}
```

**Last** — the actual mutation runs: `TaskGroupManager.UngroupWindow` removes the
window from its group and calls `ObservableCollection.Move` to relocate it, then
(after any slide-out/slide-in animations for group members that changed visibility
settle — see below) `taskbarItems.Refresh()` re-runs the `ICollectionView` filter.

This is where it gets trickier than a textbook FLIP: `Refresh()` doesn't just
reorder the existing containers — it raises a full collection
`NotifyCollectionChangedAction.Reset`, which makes the `ItemsControl` throw away
*every* container and generate brand new ones (this is called out in several other
comments in this file, e.g. around `CommitDrag`). So the `ContentPresenter`
instances captured in step First no longer exist by the time step Invert runs.
That's exactly why positions are keyed by the `ApplicationWindow` data object
(which *does* survive) rather than by container — step Invert has to re-look-up
each window's new container from scratch.

**Invert** — `AnimateLayoutChanges(oldPositions)` runs once the new containers
exist and have been measured/arranged (deferred via
`Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)`, the same pattern this
file already uses elsewhere — e.g. `TaskGroupManager.UpdateGroupVisuals` — to wait
for a layout pass to finish). For each window still present, it re-finds the
container, computes `delta = oldPosition - newPosition`, and — if the button
actually moved — sets a `TranslateTransform` to that delta up front. At this
instant the button is sitting at its new (correct) layout slot but is visually
offset back to look like it's still at its old slot:

```csharp
Point newPos = cp.TranslatePoint(new Point(0, 0), panel);
Vector delta = oldPos - newPos;
if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) continue;

cp.RenderTransform = null;
var transform = new TranslateTransform(delta.X, delta.Y);
cp.RenderTransform = transform;
```

**Play** — a 200ms `DoubleAnimation` with a `SineEase` ease-out runs the
transform's X and Y from `delta` down to `0`, so the button visibly slides from
its old position to its new one:

```csharp
transform.BeginAnimation(TranslateTransform.XProperty,
    new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
transform.BeginAnimation(TranslateTransform.YProperty,
    new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
```

Buttons that didn't move (delta under half a pixel) are left alone entirely, so an
ungroup that doesn't shift anything else in the row costs nothing extra.

## Why this isn't new infrastructure

This is the same `TranslateTransform` + `SineEase` ease-out combination already
used for the live drag-reorder snap animation elsewhere in `TaskList.xaml.cs`
(`AnimateSibling`, `GetTranslate`, `CommitDrag`'s snap-to-slot animation) — that
code is effectively hand-rolling FLIP too, just driven by live mouse movement
instead of a single before/after snapshot. `UngroupWindow`'s version is the
simplest possible case: one snapshot, one settle, one slide, no continuous
tracking.

Because the drag-reorder code also uses `ContentPresenter.RenderTransform` for its
own purposes, `StartButtonDrag` resets `RenderTransform = null` on every container
at the start of a drag — so a leftover FLIP transform from an ungroup animation is
guaranteed to be cleared before it could interfere with a later drag.
