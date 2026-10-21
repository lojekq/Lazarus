using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using Lazarus.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Lazarus.Features;

/// <summary>
/// How an entry is started again: exe and arguments (never through cmd /c, so nothing in them is interpreted), folder, verb.
/// </summary>
public sealed record Launch(string File, IReadOnlyList<string>? ArgList, string? RawArgs, string? WorkDir, bool RunAs, string? Aumid = null);

/// <summary>
/// Plans a launch, starts it, waits for the new window and puts it back in place (spec 03 §5 "Как проходит возврат").
/// Confirmation (<see cref="RiskClassifier"/>) and toasts are the caller's: the UI passes them in.
/// </summary>
public static class Restorer
{
    public static Launch Plan(Entry e, string? path = null) => e.Kind switch
    {
        EntryKind.Explorer => new("explorer.exe", null, Quote(path ?? e.Paths?.FirstOrDefault() ?? e.Active!), null, false), // quoted: Explorer splits at commas
        EntryKind.Terminal => new("wt.exe", WtArgs.Build(e.Tabs ?? []), null, null, e.Elevated),
        EntryKind.Packaged => new(e.Exe, null, PackagedArgs(e), null, false, e.Aumid),
        _ => new(e.Exe, null, Args(e), e.WorkDir, e.Elevated),
    };

    /// <summary>A shell comes back as itself, without arguments; a program with the arguments it had.</summary>
    static string Args(Entry e) =>
        e.Kind == EntryKind.Console && WindowTracker.Shells.Contains(e.ExeName) || e.CommandLine is null ? "" : CmdLine.SplitExe(e.CommandLine).RawArgs;

    static string Quote(string path) => $"\"{path}\"";

    /// <summary>
    /// A packaged app's own arguments (Notepad with a file) go to IApplicationActivationManager. A UWP app started by COM
    /// (-ServerName:…, -Embedding) has none of its own.
    /// </summary>
    static string? PackagedArgs(Entry e) =>
        e.CommandLine is null ? null : CmdLine.SplitExe(e.CommandLine).RawArgs is { Length: > 0 } a
            && !a.Contains("-ServerName:", StringComparison.OrdinalIgnoreCase) && !a.Contains("Embedding", StringComparison.OrdinalIgnoreCase) ? a : null;

    /// <summary>The full command for the confirmation dialog and "Copy command".</summary>
    public static string CommandText(Entry e)
    {
        var l = Plan(e);
        if (l.Aumid is not null) return $"shell:AppsFolder\\{l.Aumid}" + (l.RawArgs is { } a ? " " + a : "");
        var args = l.ArgList is null ? l.RawArgs : string.Join(" ", l.ArgList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        return string.IsNullOrEmpty(args) ? Quote(l.File) : $"{Quote(l.File)} {args}";
    }

    /// <summary>The exe or the folder is still there. Network and removable paths can hang for 20 s: the caller gives it 2 s.</summary>
    public static bool Exists(Entry e) => e.Kind switch
    {
        EntryKind.Explorer => e.Paths?.Any(FolderExists) == true,
        EntryKind.Terminal => true,
        _ => File.Exists(e.Exe),
    };

    public static bool FolderExists(string p) => p.StartsWith("::") || p.StartsWith("shell:") || Directory.Exists(p);

    /// <summary>Starts the launch; the pid when known (0 after ShellExecute or an Explorer window that joins the running explorer.exe).</summary>
    public static int Start(Launch l)
    {
        if (l.Aumid is not null) return Activate(l.Aumid, l.RawArgs);
        var psi = new ProcessStartInfo(l.File) { UseShellExecute = l.RunAs, WorkingDirectory = l.WorkDir ?? "" };
        if (l.RunAs) psi.Verb = "runas"; // UAC; the window will not be moved (UIPI)
        if (l.ArgList is not null) foreach (var a in l.ArgList) psi.ArgumentList.Add(a);
        else psi.Arguments = l.RawArgs ?? "";
        using var p = Process.Start(psi);
        return p?.Id ?? 0;
    }

    static int Activate(string aumid, string? args)
    {
        var m = (IApplicationActivationManager)new ApplicationActivationManager();
        m.ActivateApplication(aumid, args, ACTIVATEOPTIONS.AO_NONE, out var pid);
        return (int)pid;
    }

    /// <summary>
    /// After the start: the first new significant window of the same exe (any pid), for Explorer also with the same path,
    /// at least half the saved size (not a splash screen). null on timeout.
    /// </summary>
    public static async Task<nint?> WaitForWindow(Entry e, HashSet<nint> before, string? path, CancellationToken ct = default)
    {
        var timeout = e.Kind is EntryKind.Explorer or EntryKind.Terminal ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
        var minArea = e.Placement is { } p ? 0.5 * Area(Placement.ToRect(p.Normal)) : 0;
        var exes = new Dictionary<int, string?>();
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < timeout; await Task.Delay(250, ct))
        {
            if (e.Kind == EntryKind.Explorer) ExplorerTracker.Instance.PollNow();
            foreach (var w in TopWindows.List())
            {
                if (before.Contains(w.Hwnd) || w.Title.Length == 0 || !SameExe(e, exes.TryGetValue(w.Pid, out var x) ? x : exes[w.Pid] = ProcessInfo.ExePath(w.Pid))) continue;
                if (e.Kind == EntryKind.Explorer && (w.Class != "CabinetWClass" || !ExplorerTracker.Instance.Windows.TryGetValue(w.Hwnd, out var ex)
                    || !ex.Paths.Contains(path!, StringComparer.OrdinalIgnoreCase))) continue;
                if (Placement.Rect(w.Hwnd) is { } r && Area(r) >= minArea) return w.Hwnd;
            }
        }
        return null;
    }

    /// <summary>The windows of the entry's exe that are open before the start: a new one must not be among them.</summary>
    public static HashSet<nint> WindowsOf(Entry e)
    {
        var exes = new Dictionary<int, string?>();
        return TopWindows.List().Where(w => SameExe(e, exes.TryGetValue(w.Pid, out var x) ? x : exes[w.Pid] = ProcessInfo.ExePath(w.Pid)))
            .Select(w => w.Hwnd).ToHashSet();
    }

    /// <summary>Packaged apps (Terminal too) move with every update, so they match by file name; the rest by full path.</summary>
    static bool SameExe(Entry e, string? exe) =>
        exe is not null && (e.Kind is EntryKind.Packaged or EntryKind.Terminal
            ? Path.GetFileName(exe).Equals(e.ExeName, StringComparison.OrdinalIgnoreCase)
            : exe.Equals(e.Exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits until the window stops moving (≤ 3 s), then puts it back unless it already sits ≥ 80 % in place.</summary>
    public static async Task<bool> PutBack(nint hwnd, Place place)
    {
        var r = Placement.Rect(hwnd);
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(500);
            var now = Placement.Rect(hwnd);
            if (now == r) break;
            r = now;
        }
        if (r is { } cur && Placement.Match(Placement.ToRect(place.Rect), cur) >= Placement.InPlace) return false;
        return Placement.Apply(hwnd, place);
    }

    static double Area(Rectangle r) => (double)r.Width * r.Height;

    const uint WM_COMMAND = 0x0111, NewTabCommand = 0xA21B; // NativeFacts: Explorer's "new tab" command, sent to a tab window

    /// <summary>
    /// Opens each path as a new tab of the Explorer window, in order: "new tab" to the window, then BrowseObject in the tab that
    /// appeared. Returns how many were added; the caller opens the rest as windows.
    /// </summary>
    public static async Task<int> AddTabs(nint root, IReadOnlyList<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            var before = ExplorerTracker.Instance.Windows.GetValueOrDefault(root)?.Tabs;
            if (before is not { Count: > 0 }) break;
            PInvoke.PostMessage((HWND)before[0], WM_COMMAND, NewTabCommand, 0);
            nint fresh = 0;
            for (var sw = Stopwatch.StartNew(); fresh == 0 && sw.Elapsed < TimeSpan.FromSeconds(3); await Task.Delay(150))
            {
                ExplorerTracker.Instance.PollNow();
                fresh = ExplorerTracker.Instance.Windows.GetValueOrDefault(root)?.Tabs.Except(before).FirstOrDefault() ?? 0;
            }
            if (fresh == 0 || !await ExplorerTracker.Instance.Invoke(sw => ExplorerTracker.Navigate(sw, fresh, path))) break;
            added++;
        }
        return added;
    }

    /// <summary>
    /// Ctrl+1…8 in the Explorer window, once the user's own Ctrl, Shift, Alt and Win are up and the window is in front:
    /// the tab that was active comes back active. No key is sent anywhere else.
    /// </summary>
    public static async Task SelectTab(nint root, int index)
    {
        if (index is < 0 or > 7) return;
        for (var i = 0; i < 30 && ModifiersDown(); i++) await Task.Delay(100);
        if (ModifiersDown() || PInvoke.GetForegroundWindow() != (HWND)root) return;
        const byte VK_CONTROL = 0x11;
        var key = (byte)('1' + index);
        PInvoke.keybd_event(VK_CONTROL, 0, 0, 0);
        PInvoke.keybd_event(key, 0, 0, 0);
        PInvoke.keybd_event(key, 0, Windows.Win32.UI.Input.KeyboardAndMouse.KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
        PInvoke.keybd_event(VK_CONTROL, 0, Windows.Win32.UI.Input.KeyboardAndMouse.KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
    }

    static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>The Explorer window a tab was closed in, if it is still open.</summary>
    public static nint? LiveWindow(Entry e) =>
        e.TabOf is { } h && PInvoke.IsWindow((HWND)(nint)h) && TopWindows.ClassOf((nint)h) == "CabinetWClass" ? (nint)h : null;
}

/// <summary>Arguments for `wt.exe -w new nt … ; nt …` (NativeFacts: -p takes the GUID, `\;` keeps a semicolon). Quoting is ArgumentList's.</summary>
public static class WtArgs
{
    public static List<string> Build(IReadOnlyList<Tab> tabs)
    {
        var a = new List<string> { "-w", "new" };
        if (tabs.Count == 0) { a.Add("nt"); return a; } // no tab was found: the default profile
        foreach (var t in tabs)
        {
            if (a.Count > 2) a.Add(";");
            a.Add("nt");
            if (t.Profile is not null) a.AddRange(["-p", t.Profile]);
            if (t.Cwd is not null) a.AddRange(["-d", t.Cwd.Replace(";", @"\;")]);
            if (t.Profile is null) a.Add(t.Shell); // no profile: at least the same shell
        }
        return a;
    }
}

/// <summary>When a restore asks first (spec 03 §8.1). Empty = no question.</summary>
public static partial class RiskClassifier
{
    public const string Console = "console", Admin = "admin", Link = "link", Token = "token", Location = "location";

    public static List<string> Reasons(Entry e, IEnumerable<string> trusted, IEnumerable<string> temp, string? downloads)
    {
        var r = new List<string>();
        if (e.Elevated) r.Add(Admin); // always asks, trusted or not
        if (e.Kind is not (EntryKind.App or EntryKind.Console or EntryKind.Packaged) || trusted.Contains(e.Exe, StringComparer.OrdinalIgnoreCase)) return r;
        if (e.Kind == EntryKind.Console && !WindowTracker.Shells.Contains(e.ExeName)) r.Add(Console);
        var args = e.CommandLine is null ? [] : CmdLine.Args(CmdLine.SplitExe(e.CommandLine).RawArgs);
        if (args.Any(a => a.Contains("://"))) r.Add(Link);
        if (args.Any(LooksLikeSecret)) r.Add(Token);
        if (temp.Any(t => Paths.IsUnder(t, e.Exe)) || downloads is not null && Paths.IsUnder(downloads, e.Exe)) r.Add(Location);
        return r;
    }

    /// <summary>token=, password=, secret=, key=, code=, or a run of 33+ base64/hex characters without path separators (a GUID is fine).</summary>
    static bool LooksLikeSecret(string arg) =>
        SecretName().IsMatch(arg) || arg.Split('=', ':').Any(part => LongRun().IsMatch(part) && !Guid.TryParse(part, out _));

    [GeneratedRegex(@"(token|password|secret|key|code)=", RegexOptions.IgnoreCase)] private static partial Regex SecretName();
    [GeneratedRegex(@"^[A-Za-z0-9+_\-]{33,}={0,2}$")] private static partial Regex LongRun();
}
