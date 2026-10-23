using SecondWind.Features;

namespace SecondWind.Tests;

[TestClass]
public class JournalTests
{
    public TestContext TestContext { get; set; } = null!;

    static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.FromHours(5));

    string NewFile()
    {
        var d = Path.Combine(Path.GetTempPath(), "SecondWind.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return Path.Combine(d, "journal.json");
    }

    static Entry E(string title, double daysAgo = 0) => new() { Title = title, ClosedAt = Now.AddDays(-daysAgo), Exe = @"C:\Apps\app.exe" };

    [TestMethod]
    public void IsAStack()
    {
        var j = new Journal { Now = () => Now };
        j.Push(E("a"));
        j.Push(E("b"));
        Assert.AreEqual("b", j.Pop()!.Title);
        Assert.AreEqual("a", j.Pop()!.Title);
        Assert.IsNull(j.Pop());
    }

    [TestMethod]
    public void KeepsThe30NewestFor7Days()
    {
        var j = new Journal { Now = () => Now };
        j.Push(E("old", daysAgo: 7.01));
        for (var i = 0; i < 31; i++) j.Push(E($"n{i}") with { ClosedAt = Now.AddMinutes(i - 31) }); // apart, or they group
        Assert.HasCount(30, j.Entries);
        Assert.AreEqual("n30", j.Entries[0].Title);
        Assert.AreEqual("n1", j.Entries[^1].Title);

        var k = new Journal { Now = () => Now };
        k.Push(E("old", daysAgo: 7.01));
        k.Push(E("week", daysAgo: 6.99));
        CollectionAssert.AreEqual(new[] { "week" }, k.Entries.Select(e => e.Title).ToArray());
    }

    [TestMethod]
    public void JsonRoundTrip()
    {
        var file = NewFile();
        var j = new Journal { Now = () => Now, File = file };
        j.Push(new Entry
        {
            ClosedAt = Now, Kind = EntryKind.Terminal, Title = "pingwhen", Exe = @"C:\Program Files\WindowsApps\WT\WindowsTerminal.exe",
            Placement = new(1, [1928, 8, 2872, 1032], [1928, 8, 2872, 1032]), Monitor = "DISPLAY2",
            Tabs = [new("pwsh.exe", "{574e775e-4f2a-5b96-ac1e-a2962a402336}", @"D:\Projects\pingwhen"), new("wsl.exe", null, null)],
        });
        j.Push(new Entry { ClosedAt = Now, Kind = EntryKind.Explorer, Title = "mlink", Exe = @"C:\Windows\explorer.exe", Paths = [@"C:\a", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"], Active = @"C:\a" });
        var json = File.ReadAllText(file);
        StringAssert.Contains(json, "\"kind\": \"Terminal\"");
        StringAssert.Contains(json, "\"cwd\": \"D:\\\\Projects\\\\pingwhen\"");

        var back = new Journal { Now = () => Now, File = file };
        back.Load();
        Assert.HasCount(2, back.Entries);
        var t = back.Entries[1];
        Assert.AreEqual(EntryKind.Terminal, t.Kind);
        Assert.AreEqual("DISPLAY2", t.Monitor);
        CollectionAssert.AreEqual(new[] { 1928, 8, 2872, 1032 }, t.Placement!.Normal);
        Assert.AreEqual(new Tab("wsl.exe", null, null), t.Tabs![1]);
        Assert.AreEqual(@"C:\a", back.Entries[0].Active);
    }

    [TestMethod]
    public void CorruptFileIsKeptAsideAndTheJournalStartsEmpty()
    {
        var file = NewFile();
        File.WriteAllText(file, "{ \"version\": 1, \"entries\": [ {");
        var j = new Journal { Now = () => Now, File = file };
        j.Load();
        Assert.HasCount(0, j.Entries);
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(file)!, "journal.bad-*.json"));
        j.Push(E("new"));
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public void ThreeClosesWithin2sBecomeOneGroup()
    {
        var j = new Journal { Now = () => Now };
        j.Push(E("old", daysAgo: 0.01));
        j.Push(E("a") with { ClosedAt = Now.AddSeconds(-1.5) });
        j.Push(E("b") with { ClosedAt = Now.AddSeconds(-0.5) });
        Assert.HasCount(3, j.Entries, "two closes are two entries");
        j.Push(E("c"));
        Assert.HasCount(2, j.Entries);
        var g = j.Entries[0];
        Assert.AreEqual(EntryKind.Group, g.Kind);
        CollectionAssert.AreEqual(new[] { "c", "b", "a" }, g.Items!.Select(i => i.Title).ToArray());
        j.Push(E("d") with { ClosedAt = Now.AddSeconds(1) });
        CollectionAssert.AreEqual(new[] { "d", "c", "b", "a" }, j.Entries[0].Items!.Select(i => i.Title).ToArray(), "a 4th close joins the group");
        j.Push(E("later") with { ClosedAt = Now.AddSeconds(10) });
        CollectionAssert.AreEqual(new[] { "later", "", "old" }, j.Entries.Select(e => e.Title).ToArray());
    }

    [TestMethod]
    public void GroupSurvivesJson()
    {
        var file = NewFile();
        var j = new Journal { Now = () => Now, File = file };
        foreach (var t in new[] { "a", "b", "c" }) j.Push(E(t));
        var back = new Journal { Now = () => Now, File = file };
        back.Load();
        Assert.AreEqual(EntryKind.Group, back.Entries[0].Kind);
        Assert.HasCount(3, back.Entries[0].Items!);
    }

    [TestMethod]
    public void MemoryOnlyWritesNothing()
    {
        var file = NewFile();
        var j = new Journal { Now = () => Now };
        j.Push(E("a"));
        Assert.IsFalse(File.Exists(file));
    }
}
