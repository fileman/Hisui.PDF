using System.Runtime.InteropServices;

namespace Hisui.Pdf.App.Services;

/// <summary>
/// Publishes the recent-files list to the Windows taskbar Jump List (right-click the taskbar icon).
/// Each entry relaunches this executable with the file path as its argument, which App already opens.
/// Best-effort: any COM failure is swallowed; no-op on other platforms.
/// </summary>
internal static class WindowsJumpList
{
    public static void Update(IEnumerable<string> paths, string categoryTitle)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            var list = (ICustomDestinationList)new CDestinationList();
            var objectArrayId = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref objectArrayId, out _);

            var collection = (IObjectCollection)new CEnumerableObjectCollection();
            foreach (var path in paths.Where(File.Exists))
                collection.AddObject(CreateLink(exe, path));

            collection.GetCount(out var count);
            if (count > 0)
                list.AppendCategory(categoryTitle, (IObjectArray)collection);
            list.CommitList();
        }
        catch { /* best-effort */ }
    }

    private static IShellLinkW CreateLink(string exe, string path)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetArguments("\"" + path + "\"");
        link.SetIconLocation(exe, 0);

        var store = (IPropertyStore)link;
        var key = new PropertyKey { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };
        var title = Marshal.StringToCoTaskMemUni(Path.GetFileName(path));
        try
        {
            var value = new PropVariant { vt = 31 /* VT_LPWSTR */, ptr = title };
            store.SetValue(ref key, ref value); // PropVariant owns no memory beyond 'title', freed below
            store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(title); }
        return link;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt, r1, r2, r3;
        public IntPtr ptr;
        public IntPtr pad;
    }

    [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")] private class CDestinationList;
    [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")] private class CEnumerableObjectCollection;
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class CShellLink;

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray
    {
        new void GetCount(out uint count);
        new void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object obj);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
