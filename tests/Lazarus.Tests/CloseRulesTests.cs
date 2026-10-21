using System.Drawing;
using Lazarus.Features;

namespace Lazarus.Tests;

[TestClass]
public class CloseRulesTests
{
    static readonly DateTime T = new(2026, 10, 21, 14, 0, 0, DateTimeKind.Utc);
    static readonly Size Big = new(800, 600);
    static readonly TimeSpan Old = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void WindowFilter()
    {
        string[] none = [];
        Assert.IsTrue(Features.WindowFilter.IsSignificant("Paint", Big, false, Old, @"C:\Apps\paint.exe", none));
        Assert.IsFalse(Features.WindowFilter.IsSignificant("", Big, false, Old, @"C:\Apps\paint.exe", none), "no title");
        Assert.IsFalse(Features.WindowFilter.IsSignificant("Paint", new(119, 600), false, Old, @"C:\Apps\paint.exe", none), "narrower than 120");
        Assert.IsFalse(Features.WindowFilter.IsSignificant("Paint", Big, true, Old, @"C:\Apps\paint.exe", none), "cloaked when first seen");
        Assert.IsFalse(Features.WindowFilter.IsSignificant("Paint", Big, false, TimeSpan.FromSeconds(2.9), @"C:\Apps\paint.exe", none), "younger than 3 s");
        Assert.IsFalse(Features.WindowFilter.IsSignificant("Paint", Big, false, Old, null, none), "exe unknown");
        Assert.IsFalse(Features.WindowFilter.IsSignificant("Paint", Big, false, Old, @"C:\Apps\paint.exe", ["PAINT.EXE"]), "excluded exe");
    }

    [TestMethod]
    public void ExclusionsByNameAndByPathMask()
    {
        var p = Features.WindowFilter.BuiltIn;
        Assert.IsTrue(Features.WindowFilter.IsExcluded(@"D:\SteamLibrary\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe", p));
        Assert.IsTrue(Features.WindowFilter.IsExcluded(@"C:\Program Files\Epic Games\Fortnite\FortniteClient.exe", p));
        Assert.IsTrue(Features.WindowFilter.IsExcluded(@"C:\Windows\System32\msiexec.exe", p));
        Assert.IsTrue(Features.WindowFilter.IsExcluded(@"C:\Program Files\App\unins000.exe", p));
        Assert.IsTrue(Features.WindowFilter.IsExcluded(Path.Combine(Features.WindowFilter.TempDirs[0], @"abc\tool.exe"), p), "exe paths are long, %TEMP% may be 8.3");
        Assert.IsFalse(Features.WindowFilter.IsExcluded(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", p));
        Assert.IsTrue(Features.WindowFilter.IsExcluded(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", [@"*\obs-studio\*"]));
        Assert.IsFalse(Features.WindowFilter.IsExcluded(@"C:\steamapps\x.exe", [@"*\steamapps\common\*"]));
    }

    [TestMethod]
    public void ProcessEndedWithin15sMakesAnEntry()
    {
        Assert.AreEqual(CloseVerdict.Entry, Decide(EntryKind.App, now: T.AddSeconds(4), ended: true));
        Assert.AreEqual(CloseVerdict.Wait, Decide(EntryKind.App, now: T.AddSeconds(14), ended: false));
        Assert.AreEqual(CloseVerdict.Drop, Decide(EntryKind.App, now: T.AddSeconds(15), ended: false), "alive: tray or other windows");
        Assert.AreEqual(CloseVerdict.Drop, Decide(EntryKind.Packaged, now: T.AddSeconds(16), ended: false));
        Assert.AreEqual(CloseVerdict.Wait, Decide(EntryKind.Console, now: T.AddSeconds(1), ended: false), "a console program, not a shell");
    }

    [TestMethod]
    public void ExplorerAndConsoleShellAtOnce()
    {
        Assert.AreEqual(CloseVerdict.Entry, Decide(EntryKind.Explorer, now: T, paths: 2));
        Assert.AreEqual(CloseVerdict.Drop, Decide(EntryKind.Explorer, now: T, paths: 0));
        Assert.AreEqual(CloseVerdict.Entry, Decide(EntryKind.Console, now: T, shell: true));
    }

    [TestMethod]
    public void TerminalWaits3sAndNeedsATab()
    {
        Assert.AreEqual(CloseVerdict.Wait, Decide(EntryKind.Terminal, now: T.AddSeconds(2.9), tabs: 3));
        Assert.AreEqual(CloseVerdict.Entry, Decide(EntryKind.Terminal, now: T.AddSeconds(3), tabs: 3));
        Assert.AreEqual(CloseVerdict.Drop, Decide(EntryKind.Terminal, now: T.AddSeconds(3), tabs: 0), "tabs were dragged to another window");
    }

    [TestMethod]
    public void PauseAndSessionEndRecordNothing()
    {
        foreach (var kind in Enum.GetValues<EntryKind>())
            Assert.AreEqual(CloseVerdict.Drop, Decide(kind, now: T.AddSeconds(20), paused: true, paths: 1, tabs: 1, ended: true, shell: true));
    }

    [TestMethod]
    public void TerminalTabsAreTheShellsThatEndedAroundTheClose()
    {
        ExitedShell S(string cwd, double startedMin, double endedSec) => new(new("pwsh.exe", null, cwd), T.AddMinutes(startedMin), T.AddSeconds(endedSec));
        var tabs = CloseRules.TabsOf(T, [S("early", -9, -1.5), S("b", -5, -1), S("a", -9, 0.5), S("c", -1, 3), S("late", -9, 3.5)]);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, tabs.Select(t => t.Cwd).ToArray(), "in [−1 s; +3 s], oldest tab first");
    }

    static CloseVerdict Decide(EntryKind kind, DateTime now, bool paused = false, int paths = 0, int tabs = 0, bool ended = false, bool shell = false) =>
        CloseRules.Decide(kind, shell, T, now, paused, paths, tabs, ended);
}
