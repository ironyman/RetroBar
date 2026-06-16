Both work. For a .NET app, Visual Studio is easier:

File → Open → File... → select the .dmp file
Click "Debug with Mixed" or "Debug Managed Only" in the summary page
You get a full call stack, locals, and exception info
WinDbg is better if:

The crash is in native/unmanaged code (e.g. a ManagedShell P/Invoke)
VS can't load symbols
You want !analyze -v for automatic crash analysis
For WinDbg with a .NET app you need the SOS extension:


.loadby sos coreclr       # for .NET Core / net6
.loadby sos clr           # for .NET Framework
!analyze -v
!clrstack
!pe                       # print exception
Recommendation: Start with Visual Studio since RetroBar is a managed WPF app and VS will show you the exact exception and stack without any setup. Only reach for WinDbg if VS can't load the dump or the crash is below the managed layer.

Make sure the .pdb files from the build output are next to the .exe when you reproduce — VS will use them to resolve symbols. Since you're building locally they should already be in the output directory.

