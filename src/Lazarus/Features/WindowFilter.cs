using System.Drawing;
using System.IO;
using System.IO.Enumeration;

namespace Lazarus.Features;

/// <summary>
/// Is a window worth a journal entry (spec 03 §5). The base filter (visible, not a tool window, unowned or WS_EX_APPWINDOW,
/// not the shell, not us) is TopWindows'; this adds title, size, age, cloaked-at-first-sight and exclusions.
/// </summary>
public static class WindowFilter
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Always excluded: installers, %TEMP%, games (any Steam library, Epic), shell hosts. A pattern without "\" matches the exe
    /// file name, with "\" the full path; * and ? work, %VARS% expand.
    /// </summary>
    public static readonly string[] BuiltIn =
    [
        "msiexec.exe", "unins*.exe", "*setup*.exe", "*install*.exe", @"%TEMP%\*",
        @"*\steamapps\common\*", @"*\Epic Games\*",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe", "TextInputHost.exe",
        "LockApp.exe", "ShellHost.exe", "PickerHost.exe", "rundll32.exe", "consent.exe",
        .. DebugExclusions(),
    ];

    /// <summary>
    /// Debug builds during the parallel work (00-shared §9.4): the eight apps of the set, the build and test hosts, Claude and the
    /// folder with all the repos, so other sessions' restarts never land in the journal. The folder is found from bin\, not written here.
    /// </summary>
    static string[] DebugExclusions()
    {
#if DEBUG
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.."));
        return
        [
            "PingWhen.exe", "VramBouncer.exe", "Lazarus.exe", "EdgeTuck.exe", "Sidechain.exe", "ChangeLedger.exe", "Reclaimer.exe",
            "DownloadsLifecycle.exe", "dotnet.exe", "testhost.exe", "Claude.exe", Path.Combine(root, "*"),
        ];
#else
        return [];
#endif
    }

    public static bool IsExcluded(string exePath, IEnumerable<string> patterns) =>
        patterns.Any(p => p.Contains('\\')
            ? FileSystemName.MatchesSimpleExpression(Environment.ExpandEnvironmentVariables(p).Replace(@"\", @"\\"), exePath, ignoreCase: true) // \ escapes there
            : FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(exePath), ignoreCase: true));

    /// <summary>The facts as last seen; <paramref name="exePath"/> null = the process could not be read (another user, protected).</summary>
    public static bool IsSignificant(string title, Size size, bool cloakedAtFirstSight, TimeSpan age, string? exePath, IEnumerable<string> exclusions) =>
        title.Length > 0 && size.Width >= 120 && size.Height >= 80 && !cloakedAtFirstSight && age >= MinAge
        && exePath is not null && !IsExcluded(exePath, exclusions);
}
