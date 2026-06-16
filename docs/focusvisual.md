
The default WPF focus-visual adorner (the dashed rectangle) isn't shown just because a control IsKeyboardFocused — WPF also requires KeyboardNavigation.IsKeyboardMostRecentInputDevice to be true before it draws that adorner. That flag is updated only when WPF's own input pipeline processes a real WM_KEYDOWN/WM_KEYUP directed at the focused element.

In this code, Win+B is captured by a global low-level keyboard hook (HotkeyManager.cs/LowLevelKeyboardHook.cs), not via WPF's normal keyboard message routing. NotifyIconList.xaml.cs:104 then calls NotifyIconToggleButton.Focus() programmatically from OnFocusTrayHotkeyPressed. That sets IsKeyboardFocused = true, but since WPF never saw an actual keyboard message arrive at this window, it doesn't flip "most recent input device" to keyboard if the last thing WPF observed was mouse activity (e.g. you'd previously clicked a tray icon or the taskbar with the mouse).

So:

If the last WPF-observed input was keyboard (e.g. you tabbed around earlier), the visual shows.
If the last WPF-observed input was mouse, the visual is suppressed — even though the button genuinely has keyboard focus.
That's why it's intermittent rather than consistently broken.

The TrayToggleButton style in System.xaml:1233 (and the other theme files) doesn't define its own focus-visual trigger, so it's relying entirely on that built-in heuristic. A fix would be to add an explicit Trigger Property="IsFocused" (or IsKeyboardFocused) in the control template that draws your own focus indicator, bypassing the IsKeyboardMostRecentInputDevice check — want me to implement that?

what happened so there's the default focus visual and custom isfocused visual? what are the proper terms for these?

Two distinct WPF mechanisms, and it's worth being precise about the names:

1. The default focus visual — FocusVisualStyle adorner

Every FrameworkElement has a FocusVisualStyle property (default value is a built-in dashed-rectangle Style). When an element gets keyboard focus, WPF doesn't draw this inline — it wraps the element with an Adorner (rendered in the AdornerLayer, floating above the visual tree) using whatever Style is in FocusVisualStyle. This is the "focus rectangle" everyone means when they say "WPF focus visual."

The catch: WPF only shows that adorner when the internal check on KeyboardNavigation.IsKeyboardMostRecentInputDeviceProperty (an attached property WPF maintains by watching real WM_KEYDOWN/WM_KEYUP traffic) is true. That's the "don't show a focus rect after a mouse click" behavior baked into the framework. Since OverridesDefaultStyle="True" doesn't touch FocusVisualStyle (it's a separate property, not part of Style/Template), TrayToggleButton was still getting this default adorner — gated by that flag.

2. What I added — a template-driven visual toggled by a ControlTemplate.Trigger

This isn't an adorner at all. It's just an ordinary element (Rectangle) baked directly into the ControlTemplate's visual tree, with a Trigger Property="IsKeyboardFocused" Value="True" flipping its Visibility. This is the exact same pattern already used for IsMouseOver/IsPressed in that same template (e.g. swapping BorderBrush) — it's sometimes called a "template part" or "visual state" shown via a property trigger. It reads IsKeyboardFocused directly off the control, bypassing the IsKeyboardMostRecentInputDevice gate entirely, so it shows whenever the control truly has keyboard focus — including focus granted programmatically via Focus() from the Win+B hotkey path, which never generates the WM_KEYDOWN traffic WPF needs to flip that gate.

So: "default focus visual" = FocusVisualStyle adorner (gated, adorner-layer); the fix = an inline template element gated only by IsKeyboardFocused (ungated, part of the normal visual tree).

