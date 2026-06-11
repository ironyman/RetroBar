using ManagedShell.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace RetroBar.Utilities
{
    // COM interfaces for virtual desktop switching on Windows 11 (builds 22000-26xxx).
    // GUIDs sourced from MScholtes/VirtualDesktop (VirtualDesktop11.cs, VirtualDesktop11-24H2.cs).
    // Undocumented; may break on future Windows builds.
    internal static class VirtualDesktopHelper
    {
        private static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        private static readonly Guid CLSID_VirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        private static readonly Guid CLSID_VirtualDesktopManager = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectArray
        {
            void GetCount(out uint cObjects);
            void GetAt(uint iIndex, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        // IVirtualDesktop — Win11 21H2 through 24H2+
        [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktop
        {
            void _placeholder_IsViewVisible(); // vtable slot 3 — not called
            Guid GetId();                      // vtable slot 4
        }

        // IVirtualDesktopManagerInternal — Win11 21H2 through 24H2+
        [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManagerInternal
        {
            int GetCount();                                                                            // slot 3
            void _placeholder_MoveViewToDesktop();                                                    // slot 4
            void _placeholder_CanViewMoveDesktops();                                                  // slot 5
            IVirtualDesktop GetCurrentDesktop();                                                      // slot 6
            void GetDesktops(out IObjectArray desktops);                                              // slot 7
            [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop); // slot 8
            void SwitchDesktop(IVirtualDesktop desktop);                                              // slot 9
        }

        // Documented, stable public API — used to move windows between desktops by GUID
        [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            bool IsWindowOnCurrentVirtualDesktop(IntPtr hwnd);
            Guid GetWindowDesktopId(IntPtr hwnd);
            void MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
        }

        private static IVirtualDesktopManagerInternal GetManagerInternal()
        {
            var shellType = Type.GetTypeFromCLSID(CLSID_ImmersiveShell);
            if (shellType == null) throw new PlatformNotSupportedException("ImmersiveShell not found");
            var shell = (IServiceProvider)Activator.CreateInstance(shellType);
            Guid serviceId = CLSID_VirtualDesktopManagerInternal;
            Guid iid = typeof(IVirtualDesktopManagerInternal).GUID;
            shell.QueryService(ref serviceId, ref iid, out var obj);
            return (IVirtualDesktopManagerInternal)obj;
        }

        private static IVirtualDesktop GetDesktopAtIndex(IVirtualDesktopManagerInternal manager, int index)
        {
            manager.GetDesktops(out var desktops);
            desktops.GetCount(out uint count);
            if ((uint)index >= count) return null;
            Guid iid = typeof(IVirtualDesktop).GUID;
            desktops.GetAt((uint)index, ref iid, out var obj);
            return (IVirtualDesktop)obj;
        }

        // Switch to the nth virtual desktop (0-indexed). Returns false if out of range or unsupported.
        public static bool SwitchToDesktop(int index)
        {
            if (index < 0) return false;
            try
            {
                var manager = GetManagerInternal();
                var desktop = GetDesktopAtIndex(manager, index);
                if (desktop == null) return false;
                manager.SwitchDesktop(desktop);
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"VirtualDesktopHelper: SwitchToDesktop({index + 1}) failed - {ex.Message}");
                return false;
            }
        }

        // Move hwnd to the nth virtual desktop (0-indexed) without switching to it. Returns false if out of range or unsupported.
        public static bool MoveWindowToDesktop(IntPtr hwnd, int index)
        {
            if (index < 0 || hwnd == IntPtr.Zero) return false;
            try
            {
                var manager = GetManagerInternal();
                var desktop = GetDesktopAtIndex(manager, index);
                if (desktop == null) return false;
                Guid desktopId = desktop.GetId();
                var publicManagerType = Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager);
                if (publicManagerType == null) throw new PlatformNotSupportedException("IVirtualDesktopManager not found");
                var publicManager = (IVirtualDesktopManager)Activator.CreateInstance(publicManagerType);
                publicManager.MoveWindowToDesktop(hwnd, ref desktopId);
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"VirtualDesktopHelper: MoveWindowToDesktop({hwnd}, {index + 1}) failed - {ex.Message}");
                return false;
            }
        }
    }
}
