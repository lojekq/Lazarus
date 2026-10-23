using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SecondWind.Demo;
using SecondWind.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SecondWind.Features;

/// <summary>
/// SecondWind wired together on the UI thread: journal, trackers, hotkeys (global and the Ctrl+Shift+T one), tray items,
/// and the UI side of a restore (confirmation, toasts). Under --demo only the journal with made-up entries exists.
/// </summary>
public static class Engine
{
    public static readonly Journal Journal = new();
    public static WindowTracker? Tracker { get; private set; }

    const int GlobalId = 1, ContextId = 2;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    static readonly Hotkey CtrlShiftT = new(ModifierKeys.Control | ModifierKeys.Shift, Key.T);
    static readonly CancellationTokenSource Stop = new();
    static IDisposable? focusHook;

    /// <summary>Global hotkey status, for the settings page.</summary>
    public static HotkeyStatus HotkeyStatus { get; private set; }

    public static string HotkeyText => Settings.Current.Hotkey ?? "—";

    public static void Start()
    {
        ApplyJournalSettings(save: false); // a save here would write the still empty journal over the file before Load
        if (Args.Current.Demo)
        {
            DemoData.Fill(Journal);
            return;
        }
        Journal.Load();
        ExplorerTracker.Instance.Start(Stop.Token);
        Tracker = new WindowTracker(Journal) { Restorer = Restore, Toast = Toast };
        Tracker.Configure(Settings.Current.Exclusions);
        _ = Tracker.Run(Stop.Token);

        HotkeyStatus = ApplyHotkey();
        if (HotkeyStatus == HotkeyStatus.Busy) Toast(string.Format(Loc.T("Toast.HotkeyBusy"), HotkeyText));
        focusHook = WinEvents.Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, (_, hwnd, _, _) => FocusChanged(hwnd));
        FocusChanged(PInvoke.GetForegroundWindow());

        App.Exiting += () =>
        {
            if (Tracker is not null) Tracker.SessionEnding = true; // nothing gets recorded while everything closes
            Stop.Cancel();
        };
    }

    /// <summary>After a settings change: size, retention, keep on disk or not (off deletes the file).</summary>
    public static void ApplyJournalSettings(bool save = true)
    {
        var s = Settings.Current;
        Journal.Limit = Math.Clamp(s.JournalSize, 10, 100);
        Journal.RetentionDays = Math.Clamp(s.RetentionDays, 1, 30);
        var file = s.PersistJournal && !Args.Current.Demo ? Journal.FilePath : null;
        if (file is null && Journal.File is { } old)
            foreach (var f in new[] { old, old + ".bak" })
                try { File.Delete(f); } catch (Exception e) { Log.Warn("journal file not deleted", e); }
        Journal.File = file;
        if (save) Journal.RemoveWhere(_ => false); // prune and save with the new limits
    }

    public static HotkeyStatus ApplyHotkey()
    {
        Hotkeys.Unregister(GlobalId);
        return HotkeyStatus = Hotkey.Parse(Settings.Current.Hotkey) is { } h ? Hotkeys.Register(GlobalId, h, RestoreLast) : HotkeyStatus.Ok;
    }

    /// <summary>
    /// Ctrl+Shift+T is registered only while Explorer, the desktop or the taskbar is in front, and dropped on the next focus change,
    /// so browsers keep their own (spec 03 §8.2). A focus change also makes the trackers look at once.
    /// </summary>
    static void FocusChanged(nint hwnd)
    {
        Tracker?.Send(new WindowTracker.PollNow());
        ExplorerTracker.Instance.PollNow();
        var root = hwnd == 0 ? 0 : (nint)PInvoke.GetAncestor((HWND)hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        var shell = root != 0 && TopWindows.ClassOf(root) is "CabinetWClass" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        if (shell && Settings.Current.ContextCtrlShiftT) Hotkeys.Register(ContextId, CtrlShiftT, RestoreLast);
        else Hotkeys.Unregister(ContextId);
    }

    public static void RestoreLast()
    {
        if (Args.Current.Demo) { Toast(Loc.T("Toast.Demo")); return; }
        Tracker?.Send(new WindowTracker.RestoreLast());
    }

    public static void Restore(string id)
    {
        if (Args.Current.Demo) { Toast(Loc.T("Toast.Demo")); return; }
        Tracker?.Send(new WindowTracker.Restore(id));
    }

    /// <summary>"Open all tabs": every path of an Explorer entry as its own window; the entry stays.</summary>
    public static void OpenAll(Entry e)
    {
        if (Args.Current.Demo) { Toast(Loc.T("Toast.Demo")); return; }
        foreach (var p in e.Paths ?? [])
            try { Restorer.Start(Restorer.Plan(e, p)); }
            catch (Exception ex) { Log.Warn("open tab failed", ex); }
    }

    public static void Toast(string text) => Ui(() => Tray.Notify(AppInfo.DisplayName, text));

    static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    /// <summary>A group comes back window by window, oldest first; each waits for its window, so they don't fight over the foreground.</summary>
    static async Task Restore(Entry e)
    {
        try
        {
            if (e.Kind != EntryKind.Group) { await RestoreOne(e, toast: true); return; }
            foreach (var item in e.Items!.AsEnumerable().Reverse()) await RestoreOne(item, toast: false);
            Toast(string.Format(Loc.T("Toast.Restored"), View.Name(e)));
        }
        catch (Exception ex) { Log.Error("restore", ex); }
    }

    /// <summary>
    /// Steps 2–6 of spec 03 §5 "Как проходит возврат", off the tracker loop. A cancelled or failed restore puts the entry back on top.
    /// </summary>
    static async Task RestoreOne(Entry e, bool toast)
    {
        // A tab closed in a window that is still open goes back into that window (v1.0)
        if (e.Kind == EntryKind.Explorer && Restorer.LiveWindow(e) is { } window && await Restorer.AddTabs(window, e.Paths!) == e.Paths!.Count)
        {
            PInvoke.SetForegroundWindow((HWND)window);
            if (toast) Toast(string.Format(Loc.T("Toast.Restored"), View.Name(e)));
            Log.Info("restore Explorer tab: ok");
            return;
        }

        // Explorer: only the folders still there. A network or removable path can hang for 20 s: 2 s for all of them
        List<string>? paths = null;
        bool exists;
        try
        {
            if (e.Kind == EntryKind.Explorer) exists = (paths = await Task.Run(() => e.Paths!.Where(Restorer.FolderExists).ToList()).WaitAsync(TimeSpan.FromSeconds(2))).Count > 0;
            else exists = await Task.Run(() => Restorer.Exists(e)).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { exists = false; }
        if (!exists)
        {
            Journal.Push(e with { Error = "unavailable" });
            Toast(Loc.T(e.Kind == EntryKind.Explorer ? "Toast.FolderUnavailable" : "Toast.ExeMissing"));
            Log.Info($"restore {e.Kind} {e.ExeName}: unavailable");
            return;
        }

        var reasons = RiskClassifier.Reasons(e, Settings.Current.TrustedExes, WindowFilter.TempDirs, DownloadsFolder);
        if (reasons.Count > 0 && !await Application.Current.Dispatcher.InvokeAsync(() => ConfirmDialog.Ask(e, reasons)))
        {
            Journal.Push(e);
            return;
        }

        var path = paths?[0];
        var before = Restorer.WindowsOf(e);
        try { Restorer.Start(Restorer.Plan(e, path)); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: "No" in the UAC prompt
        {
            Journal.Push(e);
            return;
        }
        catch (Exception ex)
        {
            Journal.Push(e with { Error = "failed" });
            Toast(string.Format(Loc.T("Toast.Failed"), e.Title));
            Log.Warn($"restore {e.Kind} {e.ExeName}: start failed", ex);
            return;
        }

        if (await Restorer.WaitForWindow(e, before, path) is not { } hwnd)
        {
            // Explorer set to open folders in tabs: the folder is back, as a tab of a window that was already open
            if (path is not null && ExplorerTracker.Instance.Windows.Values.Any(w => w.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                if (toast) Toast(string.Format(Loc.T("Toast.Restored"), View.Name(e)));
                return;
            }
            var fg = (nint)PInvoke.GetForegroundWindow();
            if (toast) Toast(Loc.T(before.Contains(fg) ? "Toast.AlreadyOpen" : "Toast.NoWindow"));
            Log.Info($"restore {e.Kind} {e.ExeName}: {(before.Contains(fg) ? "already open" : "no window")}");
            return;
        }
        if (!e.Elevated && e.Placement is { } place) await Restorer.PutBack(hwnd, place); // UIPI: an elevated window can't be moved

        // Explorer's other tabs come back as tabs of the same window, the active one active again; what fails opens as windows
        if (paths is { Count: > 1 })
        {
            var rest = paths.Skip(1).ToList();
            var added = await Restorer.AddTabs(hwnd, rest);
            foreach (var p in rest.Skip(added))
                try { Restorer.Start(Restorer.Plan(e, p)); } catch (Exception ex) { Log.Warn("tab as a window failed", ex); }
            if (added == rest.Count && e.Active is { } active && paths.IndexOf(active) is > 0 and var i) await Restorer.SelectTab(hwnd, i);
            Log.Info($"restore Explorer: {added + 1} of {paths.Count} as tabs");
        }
        if (toast) Toast(string.Format(Loc.T("Toast.Restored"), View.Name(e)));
        Log.Info($"restore {e.Kind} {e.ExeName}: ok");
    }

    static readonly Lazy<string?> Downloads = new(() => { try { return Paths.Downloads; } catch { return null; } });
    public static string? DownloadsFolder => Downloads.Value;

    // ---------------------------------------------------------------- tray

    public static IEnumerable<Control> TrayItems()
    {
        var restore = Tray.Item(string.Format(Loc.T("Tray.RestoreLast"), HotkeyText), RestoreLast);

        var recent = new MenuItem { Header = Loc.T("Tray.Recent") };
        recent.Items.Add(new MenuItem()); // a placeholder, so the submenu arrow shows; filled when it opens
        recent.SubmenuOpened += (_, _) =>
        {
            recent.Items.Clear();
            foreach (var e in Journal.Entries.Take(10))
            {
                var id = e.Id;
                var item = Tray.Item(View.Name(e), () => Restore(id));
                item.Icon = View.Icon(e) is { } src ? new Image { Source = src, Width = 16, Height = 16 } : null;
                recent.Items.Add(item);
            }
            if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = Loc.T("Tray.RecentEmpty"), IsEnabled = false });
        };

        var pause = new MenuItem { Header = Loc.T("Tray.Pause") };
        MenuItem resume = null!;
        void Pause(DateTime? until)
        {
            if (Tracker is not null) Tracker.PausedUntil = until;
            pause.Header = until is null ? Loc.T("Tray.Pause") : View.PauseText(until.Value);
        }
        pause.Items.Add(Tray.Item(Loc.T("Tray.Pause15"), () => Pause(DateTime.UtcNow.AddMinutes(15))));
        pause.Items.Add(Tray.Item(Loc.T("Tray.Pause60"), () => Pause(DateTime.UtcNow.AddHours(1))));
        pause.Items.Add(Tray.Item(Loc.T("Tray.PauseRestart"), () => Pause(DateTime.MaxValue)));
        pause.Items.Add(resume = Tray.Item(Loc.T("Tray.Resume"), () => Pause(null)));
        pause.SubmenuOpened += (_, _) =>
        {
            if (Tracker?.IsPaused != true) Pause(null); // a timed pause has run out
            resume.Visibility = Tracker?.IsPaused == true ? Visibility.Visible : Visibility.Collapsed;
        };

        var settings = Tray.Item(Loc.T("Tray.Settings"), () => App.ShowMain("settings"));
        return [restore, recent, pause, settings];
    }
}
