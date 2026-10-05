Sometimes clicking on start button doesn't open open shell, but pressing win key does

I've made the change in RetroBar and it builds, but I haven't run it live yet. The running RetroBar is still the old build, and I didn't restart it.

What changed (3 files):
- RetroBar/Utilities/StartMenuMonitor.cs: added StartMenuMonitor.ToggleStartMenu(). If Open-Shell is running, it sends Open-Shell's own "toggle menu" message to Explorer's taskbar window, the same thing StartMenu.exe -toggle does. If Open-Shell isn't running, or the message can't be sent, it falls back to the old fake Win key press. The Open-Shell branch of ShowStartMenu now calls it.
- RetroBar/Controls/StartButton.xaml.cs: the non-multi-monitor click path now calls ToggleStartMenu().
- RetroBar/Taskbar.xaml.cs: DismissStartMenu() now calls ToggleStartMenu() too.

One bug I hit while testing: my first version looked up the taskbar window with FindWindow("Shell_TrayWnd"). That returned RetroBar's own window: ManagedShell's tray service creates a window with the same name, and it comes first. The code now picks the one owned by the same process as Explorer's desktop window, which is how Open-Shell finds it.

Testing so far: I sent the message directly from this shell with your elevated console in front. Six toggles in a row worked, opening and closing alternately, and the menu took focus each time. One attempt early on, just before I fixed the window lookup, didn't open the menu, and I don't know why. It hasn't happened since. I closed the menu afterwards.

Still to do: confirm the new RetroBar build end to end. Run scripts\build.ps1 -Relaunch in the RetroBar repo, focus your elevated terminal or VS Code, and click Start.

The Win+X right-click menu on the Start button still sends a fake key press, so it has the same problem when an elevated window is in front. Nothing is committed yo test it, or commit,whenever you want.