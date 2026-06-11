Good. Here's a summary of what was added and what to look for in the logs:

Added ShellLogger.Debug logging to Controls/TaskList.xaml.cs in two places:

1. In TaskList_OnSizeChanged — logs what triggered a recalculation and what TasksList.ActualWidth is at that moment.

2. In SetTaskButtonWidth() — logs:

taskCount, rows, availableWidth (TasksList.ActualWidth)
maxWidth, defaultWidth, newButtonWidth
totalWidth = taskCount * newButtonWidth and whether it overflows availableWidth
3. Post-layout Dispatcher.BeginInvoke — after WPF processes the change, logs the WrapPanel's actual rendered dimensions. If wrapPanel.ActualHeight > taskbarHeight + 1, buttons are wrapping to a second row (which gets clipped), meaning the Nth button is invisible.

To use these logs: enable Debug Logging in RetroBar's properties window, trigger the bug (open exactly N windows where the Nth disappears), then check the log file in %LOCALAPPDATA%\RetroBar\Logs\.

What to look for:

overflow=True in the calculation log → the formula itself is wrong
wrapping=True in the post-layout log but overflow=False → sub-pixel discrepancy between TasksList.ActualWidth and what the WrapPanel actually receives (likely the root cause)
availableWidth being different in SizeChanged vs CollectionChanged calls → stale width reading
