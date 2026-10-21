using System.Drawing;
using Lazarus.Features;
using Lazarus.Shell;

namespace Lazarus.Tests;

[TestClass]
public class RestorerTests
{
    const string Pwsh = "{574e775e-4f2a-5b96-ac1e-a2962a402336}";

    [TestMethod]
    public void WtArgsForSeveralTabs()
    {
        var a = WtArgs.Build([new("pwsh.exe", Pwsh, @"D:\Projects\ping when"), new("cmd.exe", null, @"C:\"), new("wsl.exe", "{0993672b-f26d-5d8b-96d8-48cd7f9beab5}", null)]);
        CollectionAssert.AreEqual(new[]
        {
            "-w", "new", "nt", "-p", Pwsh, "-d", @"D:\Projects\ping when",
            ";", "nt", "-d", @"C:\", "cmd.exe",
            ";", "nt", "-p", "{0993672b-f26d-5d8b-96d8-48cd7f9beab5}",
        }, a);
    }

    [TestMethod]
    public void WtArgsEscapeSemicolonsAndSurviveQuoting()
    {
        var a = WtArgs.Build([new("cmd.exe", Pwsh, @"C:\a b;c\"), new("cmd.exe", Pwsh, @"D:\")]);
        // One argument each; ArgumentList quotes "C:\a b\;c\" so that wt gets the trailing backslash back (CmdLineTests round-trips it)
        Assert.AreEqual(@"C:\a b\;c\", a[6]);
        Assert.AreEqual(@"D:\", a[^1]);
    }

    [TestMethod]
    public void WtArgsWithoutTabsOpenTheDefaultProfile() =>
        CollectionAssert.AreEqual(new[] { "-w", "new", "nt" }, WtArgs.Build([]));

    static Entry App(string cmd, EntryKind kind = EntryKind.App, bool elevated = false, string exe = @"C:\Apps\tool.exe") =>
        new() { Kind = kind, Exe = exe, CommandLine = cmd, Elevated = elevated };

    static List<string> Reasons(Entry e, params string[] trusted) => RiskClassifier.Reasons(e, trusted, [@"C:\Users\demo\AppData\Local\Temp"], @"C:\Users\demo\Downloads");

    [TestMethod]
    public void RiskClassifierRules()
    {
        Assert.HasCount(0, Reasons(App(@"""C:\Apps\tool.exe"" --minimize-to-tray")), "a plain GUI program");
        CollectionAssert.AreEqual(new[] { Features.RiskClassifier.Console }, Reasons(App(@"python.exe train.py --epochs 20", EntryKind.Console, exe: @"C:\Python\python.exe")));
        Assert.HasCount(0, Reasons(App(@"cmd.exe /k build.bat", EntryKind.Console, exe: @"C:\Windows\System32\cmd.exe")), "a shell comes back without its arguments");
        CollectionAssert.Contains(Reasons(App(@"tool.exe", elevated: true)), Features.RiskClassifier.Admin);
        CollectionAssert.Contains(Reasons(App(@"tool.exe --login https://example.com/cb?x=1")), Features.RiskClassifier.Link);
        CollectionAssert.Contains(Reasons(App(@"tool.exe --auth dGhpcyBpcyBhIHZlcnkgbG9uZyBzZWNyZXQgdG9rZW4x")), Features.RiskClassifier.Token);
        CollectionAssert.Contains(Reasons(App(@"tool.exe --password=hunter2")), Features.RiskClassifier.Token);
        Assert.HasCount(0, Reasons(App(@"tool.exe --profile 574e775e-4f2a-5b96-ac1e-a2962a402336")), "a GUID is not a token");
        Assert.HasCount(0, Reasons(App(@"tool.exe C:\Users\demo\Documents\a-very-long-file-name-that-is-not-a-secret.txt")), "a path is not a token");
        CollectionAssert.Contains(Reasons(App("x.exe", exe: @"C:\Users\demo\AppData\Local\Temp\x\x.exe")), Features.RiskClassifier.Location);
        CollectionAssert.Contains(Reasons(App("x.exe", exe: @"C:\Users\demo\Downloads\x.exe")), Features.RiskClassifier.Location);
    }

    [TestMethod]
    public void TrustedExeAsksOnlyForAdmin()
    {
        Assert.HasCount(0, Reasons(App("python.exe x.py", EntryKind.Console, exe: @"C:\Python\python.exe"), @"c:\python\PYTHON.EXE"));
        CollectionAssert.AreEqual(new[] { Features.RiskClassifier.Admin }, Reasons(App("python.exe x.py", EntryKind.Console, true, @"C:\Python\python.exe"), @"C:\Python\python.exe"));
    }

    [TestMethod]
    public void PlacementMatch()
    {
        var saved = new Rectangle(100, 100, 1000, 800);
        Assert.AreEqual(1.0, Placement.Match(saved, saved));
        Assert.IsGreaterThanOrEqualTo(Placement.InPlace, Placement.Match(saved, new Rectangle(120, 110, 1000, 800)), "a few pixels off: in place");
        Assert.IsLessThan(Placement.InPlace, Placement.Match(saved, new Rectangle(600, 100, 1000, 800)), "half a window off");
        Assert.IsLessThan(Placement.InPlace, Placement.Match(saved, new Rectangle(100, 100, 2000, 1400)), "much bigger");
        Assert.AreEqual(0.0, Placement.Match(saved, new Rectangle(3440, 0, 800, 600)), "another monitor");
    }

    [TestMethod]
    public void PlansNeverGoThroughAShell()
    {
        var e = App(@"""C:\Apps\tool.exe"" a ""b c"" & del x");
        var l = Restorer.Plan(e);
        Assert.AreEqual(@"C:\Apps\tool.exe", l.File);
        Assert.AreEqual(@"a ""b c"" & del x", l.RawArgs, "the tail goes as is, to the exe, not to cmd");
        Assert.AreEqual("", Restorer.Plan(App(@"""C:\Windows\System32\cmd.exe"" /k evil", EntryKind.Console, exe: @"C:\Windows\System32\cmd.exe")).RawArgs);
        var ex = Restorer.Plan(new Entry { Kind = EntryKind.Explorer, Exe = "explorer.exe", Paths = [@"C:\a,b", @"C:\c"], Active = @"C:\a,b" });
        Assert.AreEqual(@"""C:\a,b""", ex.RawArgs);
    }
}
