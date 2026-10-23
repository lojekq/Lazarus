using System.Drawing;
using System.IO;
using System.Threading.Channels;
using SecondWind.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace SecondWind.Features;

/// <summary>
/// The polling loop (spec 03 §5): once a second, and at once on a focus change or before a restore, it lists the top-level
/// windows, keeps the facts about them while their processes live (after a close they can't be read any more), applies
/// <see cref="CloseRules"/> to the windows that are gone and pushes entries to the <see cref="Journal"/>.
/// All state below is touched by the loop only; the UI talks to it through <see cref="Send"/>.
/// </summary>
public sealed class WindowTracker(Journal journal)
{
    public static readonly HashSet<string> Shells = new(["cmd.exe", "pwsh.exe", "powershell.exe", "bash.exe", "wsl.exe", "nu.exe"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Script hosts: their folder matters too (python x.py). Other programs start in their exe's folder, as Explorer starts them.</summary>
    public static readonly HashSet<string> ScriptHosts = new(["python.exe", "pythonw.exe", "node.exe", "java.exe", "javaw.exe", "dotnet.exe", "electron.exe"], StringComparer.OrdinalIgnoreCase);

    public abstract record Command;
    public sealed record RestoreLast : Command;
    public sealed record Restore(string Id) : Command;
    public sealed record PollNow : Command;

    readonly Channel<Command> commands = Channel.CreateUnbounded<Command>();
    readonly Dictionary<int, Proc> procs = [];
    readonly Dictionary<nint, Win> wins = [];
    readonly List<Pending> pending = [];
    readonly List<ExitedShell> exited = [];
    readonly List<(Win Win, nint Tab, string Path, DateTime Gone)> closedTabs = [];
    string[] exclusions = [];

    /// <summary>"Watching 23 windows".</summary>
    public int Watched { get; private set; }

    /// <summary>Pause: nothing is recorded until then (DateTime.MaxValue = until restart). UTC.</summary>
    public DateTime? PausedUntil { get; set; }
    public bool IsPaused => PausedUntil is { } p && p > DateTime.UtcNow;

    /// <summary>Windows is signing out or shutting down: dozens of windows are about to close, none of them by the user.</summary>
    public bool SessionEnding { get; set; }

    /// <summary>Restores go to the UI side (dialogs, toasts) as soon as the tracker has popped them.</summary>
    public Func<Entry, Task>? Restorer { get; set; }

    /// <summary>"&lt;app&gt; is still closing" / "Nothing to restore".</summary>
    public Action<string>? Toast { get; set; }

    public void Send(Command c) => commands.Writer.TryWrite(c);

    /// <summary>Settings changed: exclusions are applied to processes seen from now on, and the cache forgets the old verdicts.</summary>
    public void Configure(IEnumerable<string> userExclusions) => Send(new Configured([.. WindowFilter.BuiltIn, .. userExclusions]));
    sealed record Configured(string[] Exclusions) : Command;

    public Task Run(CancellationToken ct) => Task.Run(() => Loop(ct), ct);

    async Task Loop(CancellationToken ct)
    {
        var next = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(wait);
                        try { await commands.Reader.WaitToReadAsync(cts.Token); } catch (OperationCanceledException) { }
                    }
                if (ct.IsCancellationRequested) break;
                var ticked = false;
                while (commands.Reader.TryRead(out var c))
                {
                    if (c is Configured cfg) { exclusions = cfg.Exclusions; procs.Clear(); wins.Clear(); continue; }
                    if (!ticked) { Tick(); ticked = true; } // catch a window closed less than a second ago
                    Handle(c);
                }
                if (DateTime.UtcNow >= next)
                {
                    if (!ticked) Tick();
                    next = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
            catch (Exception e)
            {
                Log.Error("tracker tick", e);
                next = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            }
        }
    }

    void Handle(Command c)
    {
        Entry? e = null;
        switch (c)
        {
            case RestoreLast:
                if (pending.Count > 0) { Toast?.Invoke(string.Format(Loc.T("Toast.StillClosing"), Display(pending[^1]))); return; }
                if ((e = journal.Pop()) is null) { Toast?.Invoke(Loc.T("Toast.Nothing")); return; }
                break;
            case Restore r:
                e = journal.Take(r.Id);
                break;
        }
        if (e is not null && Restorer is { } run) _ = run(e);
    }

    static string Display(Pending p) => p.Kind switch
    {
        EntryKind.Explorer => Loc.T("Kind.Explorer"),
        EntryKind.Terminal => "Windows Terminal",
        _ => Path.GetFileNameWithoutExtension(p.Proc.Path ?? p.Proc.Name),
    };

    // ---------------------------------------------------------------- the tick

    void Tick()
    {
        var now = DateTime.UtcNow;
        var paused = IsPaused || SessionEnding;
        var table = Processes();

        // Processes that ended: a WT shell leaves its last tab behind for the window that closes around it
        foreach (var (pid, p) in procs.ToArray())
            if (!table.TryGetValue(pid, out var t) || !t.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
            {
                procs.Remove(pid);
                if (p.Tab is { } tab) exited.Add(new(tab, p.Started, now));
            }

        // WT tabs are shells whose parent is WindowsTerminal.exe (NativeFacts); their folder and profile are read every tick
        var terminals = table.Where(kv => kv.Value.Name.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToHashSet();
        foreach (var (pid, t) in table)
            if (terminals.Contains(t.Parent) && Shells.Contains(t.Name) && Get(pid, t.Name) is { Excluded: false } shell)
            {
                shell.State = Peb.Read(pid) ?? shell.State;
                shell.Tab = new(shell.Name, shell.State?.WtProfile, Folder(shell));
            }

        var displays = Monitors.Snapshot();
        var seen = new HashSet<nint>();
        foreach (var w in TopWindows.List())
        {
            seen.Add(w.Hwnd);
            if (wins.TryGetValue(w.Hwnd, out var win) && win.Pid != w.Pid) { wins.Remove(w.Hwnd); Gone(win, now, paused); win = null; }
            if (win is null)
                wins[w.Hwnd] = win = new()
                {
                    Hwnd = w.Hwnd, Pid = w.Pid, Class = w.Class, FirstSeen = now, CloakedAtFirst = w.Cloaked,
                    Proc = Get(w.Pid, table.TryGetValue(w.Pid, out var t) ? t.Name : null),
                };
            win.HiddenSince = null;
            if (win.CloakedAtFirst && !w.Cloaked)
            {
                // Cloaked when first seen, shown now: a suspended UWP frame reused by its app, a window from another desktop.
                // From here on it is a new window like any other.
                win.CloakedAtFirst = false;
                win.FirstSeen = now;
            }
            if (!win.Proc.Excluded) Update(win, w.Title, displays, now, paused);
        }

        foreach (var win in wins.Values.Where(x => !seen.Contains(x.Hwnd)).ToArray())
        {
            if (PInvoke.IsWindow((HWND)win.Hwnd) && TopWindows.Pid(win.Hwnd) == win.Pid)
            {
                win.HiddenSince ??= now; // hidden, not closed: the tray, a dialog in the way
                continue;
            }
            wins.Remove(win.Hwnd);
            Gone(win, now, paused);
        }

        foreach (var p in pending.ToArray())
        {
            var who = p.Win.AppProc ?? p.Proc;
            var ended = !table.TryGetValue(who.Pid, out var t) || !t.Name.Equals(who.Name, StringComparison.OrdinalIgnoreCase);
            var tabs = p.Kind == EntryKind.Terminal ? CloseRules.TabsOf(p.Gone, exited) : [];
            var verdict = CloseRules.Decide(p.Kind, p.IsShell, p.Gone, now, paused, p.Win.Explorer?.Paths.Count ?? 0, tabs.Count, ended);
            if (verdict == CloseVerdict.Wait) continue;
            pending.Remove(p);
            if (verdict == CloseVerdict.Entry)
            {
                if (p.Kind == EntryKind.Terminal) exited.RemoveAll(s => tabs.Contains(s.Tab)); // two WT windows closed together: no tab twice
                journal.Push(ToEntry(p, tabs));
                Log.Info($"entry {p.Kind} {(p.Win.AppProc ?? p.Proc).Name}");
            }
        }

        // A closed tab becomes an entry a tick later, if its window is still there and the tab went for good (not dragged out)
        foreach (var t in closedTabs.Where(t => now - t.Gone >= TimeSpan.FromSeconds(1)).ToArray())
        {
            closedTabs.Remove(t);
            if (paused || !wins.ContainsKey(t.Win.Hwnd) || PInvoke.IsWindow((HWND)t.Tab)) continue;
            var name = Path.GetFileName(t.Path.TrimEnd('\\'));
            journal.Push(new Entry
            {
                ClosedAt = Local(t.Gone), Kind = EntryKind.Explorer, Exe = t.Win.Proc.Path!, Placement = t.Win.Place, Monitor = t.Win.Monitor,
                Title = name.Length > 0 && !t.Path.StartsWith("::") ? name : Loc.T("Kind.Explorer"), Paths = [t.Path], Active = t.Path, TabOf = t.Win.Hwnd,
            });
            Log.Info("entry Explorer tab");
        }

        exited.RemoveAll(s => now - s.ExitedAt > TimeSpan.FromSeconds(10));
        Watched = wins.Values.Count(w => Significant(w, now));
    }

    void Update(Win win, string title, List<Display> displays, DateTime now, bool paused)
    {
        win.Title = title;
        if (Placement.Read(win.Hwnd) is { } place)
        {
            win.Place = place;
            var r = Placement.ToRect(place.Rect);
            win.Monitor = Monitors.At(new(r.Left + r.Width / 2, r.Top + r.Height / 2), displays)?.Device.Replace(@"\\.\", "");
        }
        switch (win.Class)
        {
            case "CabinetWClass" when ExplorerTracker.Instance.Windows.TryGetValue(win.Hwnd, out var ex):
                if (win.Explorer is { } old && old != ex && !paused && Significant(win, now))
                    foreach (var (tab, path) in CloseRules.ClosedTabs(old, ex, ExplorerTracker.Instance.Windows.Values.Where(o => o != ex)))
                        closedTabs.Add((win, tab, path, now));
                win.Explorer = ex;
                break;
            case "ConsoleWindowClass" when Shells.Contains(win.Proc.Name):
                win.Proc.State = Peb.Read(win.Pid) ?? win.Proc.State;
                break;
            case "ApplicationFrameWindow" when win.AppProc is null:
                // A UWP app's frame belongs to ApplicationFrameHost; the app itself owns the CoreWindow child (gone while minimized)
                var core = PInvoke.FindWindowEx((HWND)win.Hwnd, HWND.Null, "Windows.UI.Core.CoreWindow", null);
                if (core != HWND.Null) win.AppProc = Get(TopWindows.Pid(core), null);
                break;
        }
    }

    bool Significant(Win w, DateTime now) =>
        WindowFilter.IsSignificant(w.Title, w.Place is { } p ? Placement.ToRect(p.Normal).Size : Size.Empty, w.CloakedAtFirst,
            now - w.FirstSeen, w.Proc.Excluded ? null : w.Proc.Path, []);

    void Gone(Win win, DateTime now, bool paused)
    {
        if (paused || !Significant(win, now)) return;
        if (win.HiddenSince is { } h && now - h > TimeSpan.FromSeconds(2)) return; // it went to the tray earlier and exits now
        var isShell = Shells.Contains(win.Proc.Name);
        EntryKind? kind = win.Class switch
        {
            "CabinetWClass" => EntryKind.Explorer,
            "CASCADIA_HOSTING_WINDOW_CLASS" => EntryKind.Terminal,
            "ConsoleWindowClass" => EntryKind.Console,
            "ApplicationFrameWindow" => win.AppProc?.Aumid is null ? null : EntryKind.Packaged,
            _ when win.Proc.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) => null, // Run dialog, properties: not a folder
            _ when win.Proc.Aumid is not null => EntryKind.Packaged,
            _ => EntryKind.App,
        };
        if (kind is null) return;
        // One entry per process: its other windows go with it (group entries are v1.0)
        if (kind is EntryKind.App or EntryKind.Packaged or EntryKind.Console && !isShell
            && pending.Any(p => (p.Win.AppProc ?? p.Proc) == (win.AppProc ?? win.Proc))) return;
        pending.Add(new(win, win.Proc, kind.Value, isShell, now));
    }

    static Entry ToEntry(Pending p, List<Tab> tabs)
    {
        var w = p.Win;
        var proc = w.AppProc ?? p.Proc;
        var path = proc.Path!;
        var e = new Entry
        {
            ClosedAt = Local(p.Gone), Kind = p.Kind, Exe = path, Placement = w.Place, Monitor = w.Monitor, Elevated = proc.Elevated,
            Title = IsPrivate(w.Title) ? Path.GetFileNameWithoutExtension(path) : w.Title,
        };
        return p.Kind switch
        {
            EntryKind.Explorer => e with { Paths = [.. w.Explorer!.Paths], Active = w.Explorer.Active },
            EntryKind.Terminal => e with { Tabs = tabs },
            EntryKind.Packaged => e with { Aumid = proc.Aumid, CommandLine = proc.CommandLine },
            // A shell comes back as itself, never with its arguments: commands inside a shell are not repeated (spec 03 §8.1)
            EntryKind.Console => e with { CommandLine = p.IsShell ? Quote(path) : proc.CommandLine ?? Quote(path), WorkDir = Folder(proc) },
            _ => e with
            {
                CommandLine = proc.CommandLine ?? Quote(path),
                WorkDir = ScriptHosts.Contains(proc.Name) ? Folder(proc) ?? Path.GetDirectoryName(path) : Path.GetDirectoryName(path),
            },
        };
    }

    static string Quote(string path) => $"\"{path}\"";

    /// <summary>When the window went (UTC tick) as local time: a process that took 10 s to exit still sorts by its close.</summary>
    static DateTimeOffset Local(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();

    /// <summary>InPrivate and incognito windows keep only the program name.</summary>
    static bool IsPrivate(string title) =>
        title.Contains("InPrivate", StringComparison.OrdinalIgnoreCase) || title.Contains("Incognito", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Private Browsing", StringComparison.OrdinalIgnoreCase) || title.Contains("инкогнито", StringComparison.OrdinalIgnoreCase);

    /// <summary>pwsh with the hook: LAZARUS_CWD; WSL: none (a Linux path is out of reach, spec 03 §4); else the process folder.</summary>
    static string? Folder(Proc p) =>
        p.Name.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) ? null : p.State?.SecondWindCwd ?? p.State?.Cwd;

    // ---------------------------------------------------------------- processes

    /// <summary>Facts are read once per process; an excluded one (a game) gets no further handles.</summary>
    Proc Get(int pid, string? name)
    {
        if (procs.TryGetValue(pid, out var p) && (name is null || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return p;
        var path = ProcessInfo.ExePath(pid);
        p = new Proc { Pid = pid, Name = name ?? Path.GetFileName(path) ?? "", Path = path };
        if (path is null || WindowFilter.IsExcluded(path, exclusions)) p.Excluded = true;
        else
        {
            p.Started = ProcessInfo.StartTime(pid);
            p.Elevated = ProcessInfo.IsElevated(pid) ?? true; // the token could not be opened: treat it as elevated
            p.CommandLine = ProcessInfo.CommandLine(pid);
            p.Aumid = Aumid(pid);
            if (Shells.Contains(p.Name) || ScriptHosts.Contains(p.Name)) p.State = Peb.Read(pid); // VM_READ for these only (spec 03 §8.2)
        }
        procs[pid] = p;
        return p;
    }

    /// <summary>pid → (parent, exe file name) without opening any process (ProcessInfo.Tree opens each one for its start time).</summary>
    static unsafe Dictionary<int, (int Parent, string Name)> Processes()
    {
        var d = new Dictionary<int, (int, string)>();
        using var snap = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        var e = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
        for (var ok = PInvoke.Process32FirstW(snap, ref e); ok; ok = PInvoke.Process32NextW(snap, ref e))
            d[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile.ToString());
        return d;
    }

    internal static string? Aumid(int pid)
    {
        using var h = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsInvalid) return null;
        uint len = 256;
        Span<char> buf = stackalloc char[256];
        return PInvoke.GetApplicationUserModelId(h, ref len, buf) == WIN32_ERROR.ERROR_SUCCESS ? buf[..((int)len - 1)].ToString() : null;
    }

    sealed class Proc
    {
        public required int Pid;
        public required string Name;           // exe file name, as the process table shows it
        public string? Path;
        public bool Excluded;
        public DateTime? Started;
        public bool Elevated;
        public string? CommandLine;
        public string? Aumid;
        public ShellState? State;              // PEB of shells and script hosts
        public Tab? Tab;                       // set while it is a WT tab
    }

    sealed class Win
    {
        public required nint Hwnd;
        public required int Pid;
        public required string Class;
        public required DateTime FirstSeen;   // reset when a window cloaked at first sight shows up
        public required bool CloakedAtFirst;
        public required Proc Proc;
        public Proc? AppProc;                  // UWP: the app behind ApplicationFrameHost
        public string Title = "";
        public Place? Place;
        public string? Monitor;
        public ExplorerWindow? Explorer;
        public DateTime? HiddenSince;
    }

    sealed record Pending(Win Win, Proc Proc, EntryKind Kind, bool IsShell, DateTime Gone);
}
