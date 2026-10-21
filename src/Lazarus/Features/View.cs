using System.Globalization;
using System.IO;
using System.Windows.Media;
using Lazarus.Shell;
using Wpf.Ui.Controls;

namespace Lazarus.Features;

/// <summary>How entries read in the journal, the tray menu and toasts.</summary>
public static class View
{
    static bool Ru => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    /// <summary>Strings Key.One / Key.Few / Key.Many with {0} = n (Russian: 1 вкладка, 2 вкладки, 5 вкладок).</summary>
    public static string Plural(string key, int n)
    {
        var form = !Ru ? (n == 1 ? "One" : "Many")
            : n % 10 == 1 && n % 100 != 11 ? "One"
            : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "Few" : "Many";
        return string.Format(Loc.T($"{key}.{form}"), n);
    }

    /// <summary>"Explorer — pingwhen", "Windows Terminal — 3 tabs", or the window title.</summary>
    public static string Name(Entry e) => e.Kind switch
    {
        EntryKind.Explorer => $"{Loc.T("Kind.Explorer")} — {e.Title}",
        EntryKind.Terminal => $"Windows Terminal — {Plural("TermTabs", e.Tabs?.Count ?? 0)}",
        _ => e.Title.Length > 0 ? e.Title : e.ExeName,
    };

    /// <summary>The second line of a journal row.</summary>
    public static string Detail(Entry e)
    {
        switch (e.Kind)
        {
            case EntryKind.Explorer:
                var n = e.Paths?.Count ?? 0;
                return n > 1 ? $"{e.Active} · {Plural("Tabs", n - 1)}" : e.Active ?? "";
            case EntryKind.Terminal:
                var tabs = e.Tabs ?? [];
                return $"Windows Terminal · {Plural("TermTabs", tabs.Count)}" + (tabs.Count > 0 ? ": " + string.Join(", ", tabs.Select(TabName)) : "");
            case EntryKind.Packaged:
                return e.ExeName;
            default:
                var args = e.CommandLine is null ? "" : CmdLine.SplitExe(e.CommandLine).RawArgs;
                return args.Length > 0 && !(e.Kind == EntryKind.Console && WindowTracker.Shells.Contains(e.ExeName)) ? $"{e.ExeName} {args}" : e.ExeName;
        }
    }

    /// <summary>A tab by its folder name; the home folder or none is "~".</summary>
    static string TabName(Tab t) =>
        t.Cwd is null || t.Cwd.TrimEnd('\\').Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            || t.Cwd.TrimEnd('\\').EndsWith(@"\Users\demo", StringComparison.OrdinalIgnoreCase)
            ? "~" : Path.GetFileName(t.Cwd.TrimEnd('\\')) is { Length: > 0 } name ? name : t.Cwd;

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return Loc.T("Time.Now");
        if (d < TimeSpan.FromHours(1)) return string.Format(Loc.T("Time.Min"), (int)d.TotalMinutes);
        if (d < TimeSpan.FromHours(24)) return string.Format(Loc.T("Time.Hours"), (int)d.TotalHours);
        if (at.LocalDateTime.Date == now.LocalDateTime.Date.AddDays(-1)) return Loc.T("Time.Yesterday");
        return at.LocalDateTime.ToString("d MMM", CultureInfo.CurrentUICulture);
    }

    /// <summary>"DISPLAY2" → "Monitor 2".</summary>
    public static string Monitor(string? device) =>
        device is null ? "" : string.Format(Loc.T("Monitor"), new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()));

    public static string PauseText(DateTime untilUtc) =>
        untilUtc == DateTime.MaxValue ? Loc.T("Journal.PausedRestart") : string.Format(Loc.T("Journal.Paused"), untilUtc.ToLocalTime().ToString("HH:mm"));

    /// <summary>The program's own icon; null → a glyph by kind (always in --demo, so pictures don't depend on what is installed).</summary>
    public static ImageSource? Icon(Entry e) =>
        Args.Current.Demo || e.Kind is EntryKind.Explorer or EntryKind.Terminal ? null : ExeIcon.Get(e.Exe);

    public static SymbolRegular Glyph(Entry e) => e.Kind switch
    {
        EntryKind.Explorer => SymbolRegular.Folder24,
        EntryKind.Terminal or EntryKind.Console => SymbolRegular.WindowConsole20,
        _ => SymbolRegular.AppGeneric24,
    };
}

/// <summary>One journal row.</summary>
public sealed class EntryView(Entry e, DateTimeOffset now, IReadOnlyList<string> reasons)
{
    public Entry Entry { get; } = e;
    public string Title { get; } = e.Title.Length > 0 ? e.Title : e.ExeName;
    public string Detail { get; } = View.Detail(e);
    public string Time { get; } = View.Ago(e.ClosedAt, now);
    public string Monitor { get; } = View.Monitor(e.Monitor);
    public ImageSource? Icon { get; } = View.Icon(e);
    public SymbolRegular Glyph { get; } = View.Glyph(e);
    public bool NoIcon => Icon is null;
    public bool Admin { get; } = e.Elevated;
    public bool AsksFirst { get; } = reasons.Any(r => r != RiskClassifier.Admin);
    public bool Unavailable { get; } = e.Error == "unavailable";

    public bool Matches(string q) =>
        q.Length == 0 || new[] { Title, Detail, Entry.Exe, Entry.CommandLine, Entry.WorkDir }.Concat(Entry.Paths ?? []).Concat(Entry.Tabs?.Select(t => t.Cwd) ?? [])
            .Any(s => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true);
}
