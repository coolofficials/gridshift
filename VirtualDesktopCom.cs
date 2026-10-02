using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GridShift;

internal sealed class WindowsVirtualDesktopApi : IVirtualDesktopApi
{
    private const uint ClsctxLocalServer = 4;
    private static readonly Guid ImmersiveShellClsid = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid InternalManagerService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid ServiceProviderIid = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid ObjectArrayIid = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly Guid PublicManagerClsid = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid PinnedAppsService = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    private static readonly Guid PinnedAppsIid = new("4CE81583-1E4C-4632-A621-07A53543148F");
    private static readonly Guid PublicManagerIid = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
    private readonly VirtualDesktopApiKind kind;

    private WindowsVirtualDesktopApi(VirtualDesktopApiKind kind) => this.kind = kind;

    public static IVirtualDesktopApi? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var version = Environment.OSVersion.Version;
        int ubr = 0;
        if (version.Build is 26100 or 26200)
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            ubr = Convert.ToInt32(key?.GetValue("UBR", 0) ?? 0);
        }
        var kind = VirtualDesktopCompatibility.Select(version.Major, version.Minor, version.Build, ubr);
        return kind == VirtualDesktopApiKind.Unsupported ? null : new WindowsVirtualDesktopApi(kind);
    }

    public IReadOnlyList<Guid> GetDesktops()
    {
        var manager = GetInternalManager();
        IntPtr arrayPointer = IntPtr.Zero;
        object? arrayObject = null;
        try
        {
            Check(CallGetDesktops(manager, out arrayPointer), "GetDesktops");
            if (arrayPointer == IntPtr.Zero) throw new InvalidOperationException("GetDesktops returned null.");
            arrayObject = Marshal.GetTypedObjectForIUnknown(arrayPointer, typeof(IObjectArrayApi));
            var array = (IObjectArrayApi)arrayObject;
            Check(array.GetCount(out var count), "IObjectArray.GetCount");
            var result = new List<Guid>(checked((int)count));
            var iid = GetDesktopIid();
            for (uint index = 0; index < count; index++)
            {
                IntPtr desktopPointer = IntPtr.Zero;
                object? desktopObject = null;
                try
                {
                    Check(array.GetAt(index, ref iid, out desktopPointer), "IObjectArray.GetAt");
                    if (desktopPointer == IntPtr.Zero) throw new InvalidOperationException("IObjectArray.GetAt returned null.");
                    desktopObject = Marshal.GetTypedObjectForIUnknown(desktopPointer, GetDesktopInterfaceType());
                    Check(GetDesktopId(desktopObject, out var desktopId), "IVirtualDesktop.GetId");
                    result.Add(desktopId);
                }
                finally
                {
                    if (desktopObject is not null && Marshal.IsComObject(desktopObject)) Marshal.ReleaseComObject(desktopObject);
                    if (desktopPointer != IntPtr.Zero) Marshal.Release(desktopPointer);
                }
            }
            return result;
        }
        finally
        {
            if (arrayObject is not null && Marshal.IsComObject(arrayObject)) Marshal.ReleaseComObject(arrayObject);
            if (arrayPointer != IntPtr.Zero) Marshal.Release(arrayPointer);
            Release(manager);
        }
    }

    public Guid GetCurrentDesktop()
    {
        var manager = GetInternalManager();
        IntPtr desktopPointer = IntPtr.Zero;
        object? desktop = null;
        try
        {
            Check(CallGetCurrentDesktop(manager, out desktopPointer), "GetCurrentDesktop");
            if (desktopPointer == IntPtr.Zero) throw new InvalidOperationException("GetCurrentDesktop returned null.");
            desktop = Marshal.GetTypedObjectForIUnknown(desktopPointer, GetDesktopInterfaceType());
            Check(GetDesktopId(desktop, out var desktopId), "IVirtualDesktop.GetId");
            return desktopId;
        }
        finally
        {
            if (desktop is not null) Release(desktop);
            if (desktopPointer != IntPtr.Zero) Marshal.Release(desktopPointer);
            Release(manager);
        }
    }

    public Guid CreateDesktop()
    {
        var manager = GetInternalManager();
        IntPtr desktopPointer = IntPtr.Zero;
        object? desktop = null;
        try
        {
            Check(CallCreateDesktop(manager, out desktopPointer), "CreateDesktop");
            if (desktopPointer == IntPtr.Zero) throw new InvalidOperationException("CreateDesktop returned null.");
            desktop = Marshal.GetTypedObjectForIUnknown(desktopPointer, GetDesktopInterfaceType());
            Check(GetDesktopId(desktop, out var id), "IVirtualDesktop.GetId");
            return id;
        }
        finally
        {
            if (desktop is not null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if (desktopPointer != IntPtr.Zero) Marshal.Release(desktopPointer);
            Release(manager);
        }
    }

    public void SwitchTo(Guid desktopId)
    {
        var manager = GetInternalManager();
        IntPtr arrayPointer = IntPtr.Zero;
        object? arrayObject = null;
        try
        {
            Check(CallGetDesktops(manager, out arrayPointer), "GetDesktops");
            if (arrayPointer == IntPtr.Zero) throw new InvalidOperationException("GetDesktops returned null.");
            arrayObject = Marshal.GetTypedObjectForIUnknown(arrayPointer, typeof(IObjectArrayApi));
            var array = (IObjectArrayApi)arrayObject;
            Check(array.GetCount(out var count), "IObjectArray.GetCount");
            var iid = GetDesktopIid();
            for (uint index = 0; index < count; index++)
            {
                IntPtr desktopPointer = IntPtr.Zero;
                object? desktop = null;
                try
                {
                    Check(array.GetAt(index, ref iid, out desktopPointer), "IObjectArray.GetAt");
                    if (desktopPointer == IntPtr.Zero) continue;
                    desktop = Marshal.GetTypedObjectForIUnknown(desktopPointer, GetDesktopInterfaceType());
                    Check(GetDesktopId(desktop, out var id), "IVirtualDesktop.GetId");
                    if (id != desktopId) continue;
                    Check(CallSwitchDesktop(manager, desktop), "SwitchDesktop");
                    return;
                }
                finally
                {
                    if (desktop is not null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
                    if (desktopPointer != IntPtr.Zero) Marshal.Release(desktopPointer);
                }
            }
            throw new InvalidOperationException("Desktop no longer exists.");
        }
        finally
        {
            if (arrayObject is not null && Marshal.IsComObject(arrayObject)) Marshal.ReleaseComObject(arrayObject);
            if (arrayPointer != IntPtr.Zero) Marshal.Release(arrayPointer);
            Release(manager);
        }
    }

    public Guid GetWindowDesktop(IntPtr window)
    {
        var manager = CreateComObject(PublicManagerClsid, PublicManagerIid);
        try
        {
            var desktopManager = (IVirtualDesktopManagerApi)manager;
            Check(desktopManager.GetWindowDesktopId(window, out var id), "GetWindowDesktopId");
            return id;
        }
        finally { Release(manager); }
    }

    public void MoveWindowToDesktop(IntPtr window, Guid desktopId)
    {
        var manager = GetInternalManager();
        object? collection = null;
        object? pinnedApps = null;
        IntPtr view = IntPtr.Zero;
        IntPtr desktopPointer = IntPtr.Zero;
        try
        {
            collection = GetService(GetViewCollectionIid(), GetViewCollectionIid(), GetViewCollectionInterfaceType());
            pinnedApps = GetService(PinnedAppsService, PinnedAppsIid, typeof(IVirtualDesktopPinnedAppsApi));
            Check(GetViewForHwnd(collection, window, out view), "IApplicationViewCollection.GetViewForHwnd");
            if (view == IntPtr.Zero) throw new InvalidOperationException("GetViewForHwnd returned null.");
            Check(((IVirtualDesktopPinnedAppsApi)pinnedApps).IsViewPinned(view, out var pinned), "IVirtualDesktopPinnedApps.IsViewPinned");
            if (pinned) throw new InvalidOperationException("Pinned application views are not moved.");
            desktopPointer = FindDesktopPointer(desktopId);
            if (desktopPointer == IntPtr.Zero) throw new InvalidOperationException("Target desktop no longer exists.");
            Check(CallMoveViewToDesktop(manager, view, desktopPointer), "IVirtualDesktopManagerInternal.MoveViewToDesktop");
        }
        finally
        {
            if (desktopPointer != IntPtr.Zero) Marshal.Release(desktopPointer);
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (pinnedApps is not null) Release(pinnedApps);
            if (collection is not null) Release(collection);
            Release(manager);
        }
    }

    public bool IsWindowPinned(IntPtr window)
    {
        var collectionIid = GetViewCollectionIid();
        object? collection = null;
        object? pinnedApps = null;
        IntPtr viewPointer = IntPtr.Zero;
        try
        {
            collection = GetService(collectionIid, collectionIid, GetViewCollectionInterfaceType());
            pinnedApps = GetService(PinnedAppsService, PinnedAppsIid, typeof(IVirtualDesktopPinnedAppsApi));
            Check(GetViewForHwnd(collection, window, out viewPointer), "IApplicationViewCollection.GetViewForHwnd");
            if (viewPointer == IntPtr.Zero) throw new InvalidOperationException("GetViewForHwnd returned null.");
            Check(((IVirtualDesktopPinnedAppsApi)pinnedApps).IsViewPinned(viewPointer, out var pinned), "IVirtualDesktopPinnedApps.IsViewPinned");
            return pinned;
        }
        finally
        {
            if (viewPointer != IntPtr.Zero) Marshal.Release(viewPointer);
            if (pinnedApps is not null) Release(pinnedApps);
            if (collection is not null) Release(collection);
        }
    }

    private Guid GetViewCollectionIid() => kind == VirtualDesktopApiKind.Windows10
        ? new Guid("2C08ADF0-A386-4B35-9250-0FE183476FCC")
        : new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    private int GetViewForHwnd(object collection, IntPtr window, out IntPtr view) => kind == VirtualDesktopApiKind.Windows10
        ? ((IApplicationViewCollectionWin10)collection).GetViewForHwnd(window, out view)
        : ((IApplicationViewCollectionWin11)collection).GetViewForHwnd(window, out view);

    private int CallMoveViewToDesktop(object manager, IntPtr view, IntPtr desktopInterfacePointer)
        => kind == VirtualDesktopApiKind.Windows10
            ? ((IVirtualDesktopManagerInternalWin10)manager).MoveViewToDesktop(view, desktopInterfacePointer)
            : ((IVirtualDesktopManagerInternalWin11)manager).MoveViewToDesktop(view, desktopInterfacePointer);

    private IntPtr FindDesktopPointer(Guid desktopId)
    {
        var manager = GetInternalManager();
        IntPtr arrayPointer = IntPtr.Zero;
        object? arrayObject = null;
        try
        {
            Check(CallGetDesktops(manager, out arrayPointer), "GetDesktops");
            arrayObject = Marshal.GetTypedObjectForIUnknown(arrayPointer, typeof(IObjectArrayApi));
            var array = (IObjectArrayApi)arrayObject;
            Check(array.GetCount(out var count), "IObjectArray.GetCount");
            var iid = GetDesktopIid();
            for (uint index = 0; index < count; index++)
            {
                Check(array.GetAt(index, ref iid, out var candidate), "IObjectArray.GetAt");
                if (candidate == IntPtr.Zero) continue;
                object? candidateObject = null;
                var found = false;
                try
                {
                    candidateObject = Marshal.GetTypedObjectForIUnknown(candidate, GetDesktopInterfaceType());
                    Check(GetDesktopId(candidateObject, out var id), "IVirtualDesktop.GetId");
                    found = id == desktopId;
                    if (found) return candidate;
                }
                finally
                {
                    if (candidateObject is not null) Release(candidateObject);
                    if (candidate != IntPtr.Zero && !found) Marshal.Release(candidate);
                }
            }
            return IntPtr.Zero;
        }
        finally
        {
            if (arrayObject is not null) Release(arrayObject);
            if (arrayPointer != IntPtr.Zero) Marshal.Release(arrayPointer);
            Release(manager);
        }
    }

    private object GetInternalManager() => GetService(InternalManagerService, GetInternalManagerIid(), GetInternalManagerInterfaceType());

    private object GetService(Guid serviceId, Guid iid, Type interfaceType)
    {
        var shell = CreateComObject(ImmersiveShellClsid, ServiceProviderIid, ClsctxLocalServer);
        IntPtr servicePointer = IntPtr.Zero;
        try
        {
            Check(((IServiceProviderApi)shell).QueryService(ref serviceId, ref iid, out servicePointer), "IServiceProvider.QueryService");
            if (servicePointer == IntPtr.Zero) throw new InvalidOperationException("QueryService returned null.");
            return Marshal.GetTypedObjectForIUnknown(servicePointer, interfaceType);
        }
        finally
        {
            if (servicePointer != IntPtr.Zero) Marshal.Release(servicePointer);
            Release(shell);
        }
    }

    private object CreateComObject(Guid clsid, Guid iid, uint context = 1 | ClsctxLocalServer)
    {
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, context, ref iid, out var pointer), "CoCreateInstance");
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("CoCreateInstance returned null.");
        try { return Marshal.GetTypedObjectForIUnknown(pointer, GetTypeForIid(iid)); }
        finally { Marshal.Release(pointer); }
    }

    private Type GetTypeForIid(Guid iid)
        => iid == ServiceProviderIid ? typeof(IServiceProviderApi) : typeof(IVirtualDesktopManagerApi);

    private Guid GetInternalManagerIid() => kind switch
    {
        VirtualDesktopApiKind.Windows10 => new Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"),
        _ => new Guid("53F5CA0B-158F-4124-900C-057158060B27")
    };

    private Guid GetDesktopIid() => kind switch
    {
        VirtualDesktopApiKind.Windows10 => new Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4"),
        _ => new Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")
    };

    private Type GetInternalManagerInterfaceType() => kind switch
    {
        VirtualDesktopApiKind.Windows10 => typeof(IVirtualDesktopManagerInternalWin10),
        _ => typeof(IVirtualDesktopManagerInternalWin11)
    };

    private Type GetDesktopInterfaceType() => kind switch
    {
        VirtualDesktopApiKind.Windows10 => typeof(IVirtualDesktopWin10),
        _ => typeof(IVirtualDesktopWin11)
    };

    private Type GetViewCollectionInterfaceType() => kind switch
    {
        VirtualDesktopApiKind.Windows10 => typeof(IApplicationViewCollectionWin10),
        _ => typeof(IApplicationViewCollectionWin11)
    };

    private int CallGetCurrentDesktop(object manager, out IntPtr desktop) => kind switch
    {
        VirtualDesktopApiKind.Windows10 => ((IVirtualDesktopManagerInternalWin10)manager).GetCurrentDesktop(out desktop),
        _ => ((IVirtualDesktopManagerInternalWin11)manager).GetCurrentDesktop(out desktop)
    };

    private int CallGetDesktops(object manager, out IntPtr desktops) => kind switch
    {
        VirtualDesktopApiKind.Windows10 => ((IVirtualDesktopManagerInternalWin10)manager).GetDesktops(out desktops),
        _ => ((IVirtualDesktopManagerInternalWin11)manager).GetDesktops(out desktops)
    };

    private int CallCreateDesktop(object manager, out IntPtr desktop) => kind switch
    {
        VirtualDesktopApiKind.Windows10 => ((IVirtualDesktopManagerInternalWin10)manager).CreateDesktop(out desktop),
        _ => ((IVirtualDesktopManagerInternalWin11)manager).CreateDesktop(out desktop)
    };

    private int CallSwitchDesktop(object manager, object desktop) => kind switch
    {
        VirtualDesktopApiKind.Windows10 => ((IVirtualDesktopManagerInternalWin10)manager).SwitchDesktop((IVirtualDesktopWin10)desktop),
        _ => ((IVirtualDesktopManagerInternalWin11)manager).SwitchDesktop((IVirtualDesktopWin11)desktop)
    };

    private int GetDesktopId(object desktop, out Guid id) => kind switch
    {
        VirtualDesktopApiKind.Windows10 => ((IVirtualDesktopWin10)desktop).GetId(out id),
        _ => ((IVirtualDesktopWin11)desktop).GetId(out id)
    };

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult, new IntPtr(-1));
    }

    private static void Release(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderApi
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArrayApi
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerApi
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid desktopId);
    }

    [ComImport, Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopWin10
    {
        [PreserveSig] int IsViewVisible(IntPtr view, [MarshalAs(UnmanagedType.Bool)] out bool visible);
        [PreserveSig] int GetId(out Guid id);
    }

    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopWin11
    {
        [PreserveSig] int IsViewVisible(IntPtr view, out uint visible);
        [PreserveSig] int GetId(out Guid id);
        [PreserveSig] int GetName(IntPtr name);
        [PreserveSig] int GetWallpaper(IntPtr wallpaper);
    }

    [ComImport, Guid("2C08ADF0-A386-4B35-9250-0FE183476FCC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollectionWin10
    {
        [PreserveSig] int GetViews(out IntPtr views);
        [PreserveSig] int GetViewsByZOrder(out IntPtr views);
        [PreserveSig] int GetViewsByAppUserModelId(IntPtr appId, out IntPtr views);
        [PreserveSig] int GetViewForHwnd(IntPtr window, out IntPtr view);
    }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollectionWin11
    {
        [PreserveSig] int GetViews(out IntPtr views);
        [PreserveSig] int GetViewsByZOrder(out IntPtr views);
        [PreserveSig] int GetViewsByAppUserModelId(IntPtr appId, out IntPtr views);
        [PreserveSig] int GetViewForHwnd(IntPtr window, out IntPtr view);
    }

    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedAppsApi
    {
        [PreserveSig] int IsAppPinned(IntPtr appId, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinApp(IntPtr appId);
        [PreserveSig] int UnpinApp(IntPtr appId);
        [PreserveSig] int IsViewPinned(IntPtr view, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
    }

    [ComImport, Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternalWin10
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int MoveViewToDesktop(IntPtr view, IntPtr desktop);
        [PreserveSig] int CanViewMoveDesktops(IntPtr view, IntPtr canMove);
        [PreserveSig] int GetCurrentDesktop(out IntPtr desktop);
        [PreserveSig] int GetDesktops(out IntPtr desktops);
        [PreserveSig] int GetAdjacentDesktop(IntPtr desktop, int direction, out IntPtr adjacent);
        [PreserveSig] int SwitchDesktop([MarshalAs(UnmanagedType.Interface)] IVirtualDesktopWin10 desktop);
        [PreserveSig] int CreateDesktop(out IntPtr desktop);
    }

    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternalWin11
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int MoveViewToDesktop(IntPtr view, IntPtr desktop);
        [PreserveSig] int CanMoveViewBetweenDesktops(IntPtr view, IntPtr canMove);
        [PreserveSig] int GetCurrentDesktop(out IntPtr desktop);
        [PreserveSig] int GetDesktops(out IntPtr desktops);
        [PreserveSig] int GetAdjacentDesktop(IntPtr desktop, uint direction, out IntPtr adjacent);
        [PreserveSig] int SwitchDesktop([MarshalAs(UnmanagedType.Interface)] IVirtualDesktopWin11 desktop);
        [PreserveSig] int SwitchDesktopAndMoveForegroundView([MarshalAs(UnmanagedType.Interface)] IVirtualDesktopWin11 desktop);
        [PreserveSig] int CreateDesktop(out IntPtr desktop);
    }
}
