using System.Collections.Frozen;
using System.Runtime.InteropServices;
using SecondWind.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SecondWind.Features;

/// <summary>
/// One Explorer window: the paths of its tabs (parsing names, special folders as ::{CLSID}) in tab order, the active one,
/// and each tab's ShellTabWindowClass handle (same order), which stays the same while the tab navigates.
/// </summary>
public sealed record ExplorerWindow(IReadOnlyList<string> Paths, string Active, IReadOnlyList<nint> Tabs);

/// <summary>
/// Own STA thread: once a second IShellWindows → {CabinetWClass hwnd → tabs}. The snapshot is immutable and swapped atomically,
/// so a hung Explorer stops only this thread and the tracker keeps the last snapshot (watchdog: <see cref="Age"/>).
/// </summary>
public sealed class ExplorerTracker
{
    public static readonly ExplorerTracker Instance = new();

    volatile Snap snap = new(FrozenDictionary<nint, ExplorerWindow>.Empty, DateTime.UtcNow);
    readonly AutoResetEvent wake = new(false);
    readonly System.Collections.Concurrent.ConcurrentQueue<Action<IShellWindows>> work = new();
    Thread? thread;

    sealed record Snap(FrozenDictionary<nint, ExplorerWindow> Windows, DateTime At);

    public FrozenDictionary<nint, ExplorerWindow> Windows => snap.Windows;

    /// <summary>Over 10 s: "Explorer is not responding".</summary>
    public TimeSpan Age => DateTime.UtcNow - snap.At;

    public void Start(CancellationToken ct)
    {
        thread = new Thread(() => Loop(ct)) { IsBackground = true, Name = "ExplorerTracker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Take a snapshot now (before a restore, after a focus change).</summary>
    public void PollNow() => wake.Set();

    /// <summary>Runs on the STA thread with the live ShellWindows (COM calls into Explorer must come from there).</summary>
    internal Task<T> Invoke<T>(Func<IShellWindows, T> f)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Enqueue(sw =>
        {
            try { done.SetResult(f(sw)); }
            catch (Exception e) { done.SetException(e); }
        });
        wake.Set();
        return done.Task;
    }

    /// <summary>
    /// The tab goes to the path, in place (IShellBrowser.BrowseObject, SBSP_SAMEBROWSER). False if the tab is not found
    /// or the path does not parse.
    /// </summary>
    internal static unsafe bool Navigate(IShellWindows shell, nint tab, string path)
    {
        if (PInvoke.SHParseDisplayName(path, null, out var pidl, 0, out _).Failed) return false;
        try
        {
            for (var i = 0; i < shell.Count; i++)
            {
                object? item = null;
                try
                {
                    item = shell.Item(i);
                    if (item is not IServiceProvider sp) continue;
                    var browser = (IShellBrowser)sp.QueryService(ShellCom.SID_STopLevelBrowser, typeof(IShellBrowser).GUID);
                    if (browser.GetWindow() != tab) continue;
                    browser.BrowseObject((nint)pidl, 0x0001); // SBSP_SAMEBROWSER | SBSP_ABSOLUTE
                    return true;
                }
                catch { }
                finally { if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
            }
            return false;
        }
        finally { PInvoke.CoTaskMemFree(pidl); }
    }

    void Loop(CancellationToken ct)
    {
        IShellWindows? shell = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                shell ??= (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellCom.CLSID_ShellWindows)!)!;
                while (work.TryDequeue(out var w)) w(shell);
                snap = new(Take(shell).ToFrozenDictionary(), DateTime.UtcNow);
            }
            catch (Exception e)
            {
                // RPC_E_DISCONNECTED and friends: Explorer restarted, the object is dead
                Log.Warn($"shell windows: 0x{e.HResult:X8}");
                if (shell is not null) Marshal.ReleaseComObject(shell);
                shell = null;
            }
            WaitHandle.WaitAny([wake, ct.WaitHandle], 1000);
        }
    }

    /// <summary>One pass over IShellWindows. A tab that fails is skipped, the others still count.</summary>
    internal static Dictionary<nint, ExplorerWindow> Take(IShellWindows shell)
    {
        var tabs = new List<(nint Root, nint Tab, string Path)>();
        for (var i = 0; i < shell.Count; i++)
        {
            object? item = null;
            try
            {
                item = shell.Item(i);
                if (item is not IServiceProvider sp) continue;
                var browser = (IShellBrowser)sp.QueryService(ShellCom.SID_STopLevelBrowser, typeof(IShellBrowser).GUID);
                var tabHwnd = browser.GetWindow();
                nint root = PInvoke.GetAncestor((HWND)tabHwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
                if (TopWindows.ClassOf(root) != "CabinetWClass") continue; // Internet Explorer leftovers, desktop
                if (Path((IFolderView)browser.QueryActiveShellView()) is { } path) tabs.Add((root, tabHwnd, path));
            }
            catch { } // a tab in the middle of navigation
            finally { if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
        }
        return tabs.GroupBy(t => t.Root).ToDictionary(g => g.Key, g =>
        {
            var activeTab = (nint)PInvoke.FindWindowEx((HWND)g.Key, HWND.Null, NativeFacts.ShellTabClass, null);
            var active = g.FirstOrDefault(t => t.Tab == activeTab).Path ?? g.First().Path;
            return new ExplorerWindow(g.Select(t => t.Path).ToList(), active, g.Select(t => t.Tab).ToList());
        });
    }

    static unsafe string? Path(IFolderView view)
    {
        var pidl = ((IPersistFolder2)view.GetFolder(typeof(IPersistFolder2).GUID)).GetCurFolder();
        try
        {
            PInvoke.SHGetNameFromIDList(in *(ITEMIDLIST*)pidl, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING, out var name).ThrowOnFailure();
            try { return name.ToString(); }
            finally { PInvoke.CoTaskMemFree(name.Value); }
        }
        finally { PInvoke.CoTaskMemFree((void*)pidl); }
    }
}

// Hand-written COM: CsWin32 can't generate IShellBrowser and IShellView for AnyCPU. Only the slots we call have real signatures;
// the others keep the vtable order.
static class ShellCom
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
}

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IShellWindows
{
    int Count { get; }
    [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IServiceProvider
{
    [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(in Guid service, in Guid riid);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellBrowser
{
    nint GetWindow();
    void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB();
    void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject(nint pidl, uint flags); void GetViewStateStream(); void GetControlWindow();
    void SendControlMsg();
    [return: MarshalAs(UnmanagedType.IUnknown)] object QueryActiveShellView();
}

[ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFolderView
{
    void GetCurrentViewMode(); void SetCurrentViewMode();
    [return: MarshalAs(UnmanagedType.IUnknown)] object GetFolder(in Guid riid);
}

[ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPersistFolder2
{
    void GetClassID(); void Initialize();
    nint GetCurFolder();
}
