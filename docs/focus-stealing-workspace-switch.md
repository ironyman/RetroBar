What was corrupting the remembered focus
Traced it precisely in the new log (the 0xA0852 → 0x608CE corruption around 10:51:51):

Switching back to ws1, windows re-show one by one. Each fires an activation, all correctly ignored as incidental.
The old code restored focus mid-batch — BringToFront(0xA0852) at line 5577.
But 0x608CE (VS Code / todo.txt, which calls SetForegroundWindow on itself when un-hidden) finished showing after that and stole the foreground back (line 5627).
By then the 500 ms activation-suppression — measured from the last hide/show — had lapsed, so Window_PropertyChanged recorded 0x608CE as ws1's last-active (line 5625).
The next leave-snapshot faithfully captured that corrupted 0x608CE, so switching back kept restoring the wrong window.
So the restore was both applied too early (later re-shows beat it) and recorded too late (a stray steal got saved as your choice).

The fix
Focus restoration is now deferred until the whole re-show batch has settled, then posted at Background dispatcher priority so it runs after the synchronous re-adds and after any self-activating app — making our activation the last word (WorkspaceManager.cs):

SwitchToWorkspace only queues _pendingActivate; it no longer calls BringToFront inline.
The Add handler schedules the flush once _pendingShow drains to empty.
FlushPendingActivate brings the target to front, records it directly as the workspace's last-active, and re-arms suppression so an immediate self-activation afterward is treated as incidental instead of overwriting the memory.
Genuine user clicks are still captured — after suppression lapses, or via the leave-time foreground snapshot (the backstop).

do you have to fire an activation when you show the windows? if you do can't you just show the workspace last focused window after all the other ones?

Good question — and it gets at whether the deferred BringToFront is even necessary. Let me clarify what's actually happening, because we're already doing part of what you suggest.

We don't fire activations on purpose. Every re-show goes through SetWindowVisible → ShowWindow(hwnd, SW_SHOWNOACTIVATE) (WorkspaceManager.cs:272) — the explicit "show but don't steal focus" flag. So the Activated window events in the log aren't us. They come from two places:

The OS still emits HSHELL_WINDOWACTIVATED for some windows when they're un-hidden, even with SW_SHOWNOACTIVATE.
The app itself — 0x608CE is VS Code, and Electron apps call SetForegroundWindow on themselves when un-hidden. That's the one that actually stole focus at log line 5627.
Your idea — show the target last — is right, and it's a better primary mechanism than a separate BringToFront. If I show every other window with NO­ACTIVATE and then show the last-focused window with an activating SW_SHOW as the final call in the loop, its activation is part of the ordered show sequence instead of a separate racing call. For the passive/OS-level cases, that fully fixes it with no dispatcher gymnastics.

But it can't stand alone, and here's the catch that created this whole mess: VS Code doesn't self-activate synchronously during its ShowWindow. It posts a SetForegroundWindow that Windows delivers after our show loop has already returned — so no matter what order we showed things in, that steal lands last. That async, after-the-fact steal is the only reason the deferred re-assert exists.

So the robust design is both:

Show the target last (activating) — the clean primary path; handles OS/Z-order races with zero timing tricks.
Keep a lightweight deferred flush — purely as insurance against apps that grab focus via a posted message after our loop, and to keep _lastActiveByWorkspace honest so a late steal can't corrupt the memory (the actual bug you reported).
Neither alone is enough for a determined self-activator like VS Code; together they cover it.

how is vscode able to steal focus when its process doesn't have input focus? If retrobar just handled a hotkey doesn't retrobar thread have input? Isn't foreground stealing prevention supposed to stop other threads from setforeground when we hav eit

The premise is right that Win+1-9 go through RegisterHotKey → WM_HOTKEY (HotkeyManager.cs:559), and the code already knows WM_HOTKEY grants a foreground token (see the comments at HotkeyManager.cs:113-115). So RetroBar does get the privilege. The reason VS Code still wins is about what that privilege actually is.

Foreground-stealing prevention isn't a lock you hold — it's a per-call allow-list
SetForegroundWindow from a background process is normally refused, unless the caller meets one of a set of exceptions. The relevant ones (from the Win32 docs):

the process received the last input event (WM_HOTKEY counts — that's RetroBar's token),
the process called AllowSetForegroundWindow,
there is no foreground window, or the foreground window is being destroyed/hidden.
That last one is the killer. Your WM_HOTKEY token grants RetroBar an exception; it does not install a barrier that blocks anyone else. VS Code doesn't need to beat your token — it just needs to independently qualify for an exception of its own. And you hand it one.

Your own hide-loop disarms the protection
SwitchToWorkspace hides the departing workspace's windows first — including the one that was foreground. The moment that window hides, the foreground goes to NULL. In a foreground vacuum, the prevention rule is suspended and any process's SetForegroundWindow succeeds. VS Code (Electron) fires one when it receives WM_SHOWWINDOW on being un-hidden, drops into that vacuum, and legitimately takes foreground. No privilege violation — Windows considers it fair game because there was nothing to steal from.

So it's not that VS Code out-ranks you; it's that between "hide old foreground" and "activate target" you create a window where the rule that would protect your target isn't in effect.

Two more reasons your token doesn't save you
It's one-shot and short-lived. The WM_HOTKEY foreground permission is consumed the first time the foreground changes (and expires after a moment). By the time VS Code's steal lands — it's posted, delivered after your synchronous switch returns — your token is likely already spent. That's exactly why makeForeground resorts to the AttachThreadInput trick (ApplicationWindow.cs:697-708): borrow the current foreground thread's input state because RetroBar no longer qualifies on its own.
The deferred re-assert races the same way. Even my Background-priority flush can lose if VS Code's posted grab is processed after it.
The API-sanctioned fix: LockSetForegroundWindow
There is a way to hold it, which is what you're reaching for. While RetroBar still has the WM_HOTKEY token (at the very top of the hotkey handler, before any hide/show):


LockSetForegroundWindow(LSFW_LOCK);   // globally disables SetForegroundWindow for everyone
//   ... hide old workspace, show new workspace (VS Code's self-activation is now refused) ...
LockSetForegroundWindow(LSFW_UNLOCK);
target.BringToFront();                 // we set foreground last, uncontested
While locked, VS Code's SetForegroundWindow is denied rather than deferred, so it can't grab the vacuum. This is strictly better than the flush hack for the hotkey path.

Caveats (why it's not a total silver bullet):

The lock auto-releases on real user input and some system events — fine for a fast programmatic switch.
It only helps the hotkey path, where RetroBar holds the token. The taskbar-click path (clicking a workspace button) is weaker: the click is user input that can break the lock, and RetroBar's taskbar window may itself be foreground at that point. There you'd still lean on the deferred flush.
Net recommendation: add LockSetForegroundWindow(LSFW_LOCK/UNLOCK) around the switch as the primary defense, keep the deferred flush + _lastActiveByWorkspace bookkeeping as the safety net for the mouse path and stubborn stragglers.

Want me to wire in LockSetForegroundWindow? It's a small P/Invoke plus a lock/unlock pair around the hide/show in SwitchToWorkspace (I'll check whether it's already in the interop layer).

