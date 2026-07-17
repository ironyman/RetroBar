2. Should Tabame ever be in the taskbar? No — and I found why it was.

quickmenu.dart tries to hide the popup two ways:

WindowManager.instance.setSkipTaskbar(true) (line 50) — but this only calls Windows' ITaskbarList3::DeleteTab, which only Explorer's real taskbar honors. RetroBar doesn't use that API at all — it enumerates windows via shell hooks and checks styles directly (ApplicationWindow.CanAddToTaskbar, which requires WS_EX_TOOLWINDOW).
The actual style patch that RetroBar would respect is gated: if (kReleaseMode) { SetWindowLongPtr(..., WS_EX_TOOLWINDOW ...) } (quickmenu.dart:59-66). kReleaseMode is only true in a true flutter build windows --release binary — it's false in both debug and profile builds.
So if the Tabame instance you were running wasn't a genuine release build, it never got WS_EX_TOOLWINDOW, stayed a normal WS_EX_APPWINDOW-style top-level window, and RetroBar correctly (per its own rules) treated it as a real taskbar app. Fix belongs in tabamewin32/lib/pages/quickmenu.dart: apply the WS_EX_TOOLWINDOW exstyle unconditionally (or at least whenever !kDebugMode isn't the right gate — really it should just always run, since a taskbar-hidden popup should never depend on build flavor).

Why Tabame uses DeleteTab
window_manager (the cross-platform Flutter window plugin Tabame uses) implements setSkipTaskbar() on Windows purely as ITaskbarList3::DeleteTab(hwnd). That's the documented, blessed Microsoft API for "hide me from the taskbar" — it's what any Windows app following the official guidance calls, and it's genuinely the right call for Tabame to make (unlike WS_EX_TOOLWINDOW, DeleteTab doesn't also mess with Alt+Tab behavior). Tabame isn't doing anything unusual — RetroBar is just a taskbar that most of the ecosystem has no reason to specifically know about.
