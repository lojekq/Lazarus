namespace SecondWind.Features;

public enum CloseVerdict { Entry, Wait, Drop }

/// <summary>A WT shell that ended: its last known tab, when it started (tab order) and the tick that saw it gone.</summary>
public sealed record ExitedShell(Tab Tab, DateTime? Started, DateTime ExitedAt);

/// <summary>
/// What to do with a window that is gone (spec 03 §5, "close rule"). Pure: the tracker passes in what it knows.
/// Explorer: an entry at once if it had a path. WT: wait 3 s, then the shells that ended in [−1 s; +3 s] are its tabs;
/// none → the tabs were dragged to another window, no entry. Console with a shell: at once. Everything else:
/// an entry only if the process ended within 15 s; alive → it went to the tray or keeps other windows.
/// </summary>
public static class CloseRules
{
    public static readonly TimeSpan TerminalWait = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TabsBefore = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ProcessWait = TimeSpan.FromSeconds(15);

    /// <param name="gone">the tick that found the window gone</param>
    /// <param name="paused">pause, Windows session ending: nothing is recorded</param>
    public static CloseVerdict Decide(EntryKind kind, bool isShell, DateTime gone, DateTime now, bool paused,
        int explorerPaths, int terminalTabs, bool processEnded)
    {
        if (paused) return CloseVerdict.Drop;
        switch (kind)
        {
            case EntryKind.Explorer:
                return explorerPaths > 0 ? CloseVerdict.Entry : CloseVerdict.Drop;
            case EntryKind.Terminal:
                if (now - gone < TerminalWait) return CloseVerdict.Wait;
                return terminalTabs > 0 ? CloseVerdict.Entry : CloseVerdict.Drop;
            case EntryKind.Console when isShell:
                return CloseVerdict.Entry;
            default:
                if (processEnded) return CloseVerdict.Entry;
                return now - gone < ProcessWait ? CloseVerdict.Wait : CloseVerdict.Drop;
        }
    }

    /// <summary>
    /// All WT windows share one process, so a closed window's tabs are found by time: shells that ended in [gone − 1 s; gone + 3 s],
    /// in the order they started (tabs opened earlier sit further left).
    /// </summary>
    public static List<Tab> TabsOf(DateTime gone, IEnumerable<ExitedShell> exited) =>
        exited.Where(s => s.ExitedAt >= gone - TabsBefore && s.ExitedAt <= gone + TerminalWait).OrderBy(s => s.Started).Select(s => s.Tab).ToList();

    /// <summary>
    /// Tabs that left an Explorer window between two snapshots, with their last path. A tab handle found in another window
    /// was dragged there, not closed. The tracker still waits a tick: if the whole window goes, its own entry covers the tabs.
    /// </summary>
    public static List<(nint Tab, string Path)> ClosedTabs(ExplorerWindow before, ExplorerWindow now, IEnumerable<ExplorerWindow> others) =>
        before.Tabs.Zip(before.Paths).Where(t => !now.Tabs.Contains(t.First) && !others.Any(o => o.Tabs.Contains(t.First))).ToList();
}
