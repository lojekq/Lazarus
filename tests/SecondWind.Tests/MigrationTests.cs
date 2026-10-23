using Microsoft.Win32;
using SecondWind.Features;

namespace SecondWind.Tests;

/// <summary>Lazarus → Second Wind on a fake old installation: temp folders and a throwaway HKCU\Software\lojekq-tests key.</summary>
[TestClass]
public class MigrationTests
{
    public TestContext TestContext { get; set; } = null!;

    string root = "", reg = "";

    [TestInitialize]
    public void Init()
    {
        root = Path.Combine(Path.GetTempPath(), "SecondWind.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        reg = @"Software\lojekq-tests\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup() => Registry.CurrentUser.DeleteSubKeyTree(reg, false);

    string P(string rel) => Path.Combine(root, rel);

    Migration.Old Old(bool system = true) => new(
        P(@"AppData\Lazarus"), P(@"Local\Lazarus"), system ? P(@"Local\Programs\Lazarus") : null, "Lazarus-test-" + Guid.NewGuid().ToString("N")[..8],
        system ? reg + @"\Run" : null, system ? reg + @"\Approved" : null, system ? reg + @"\Uninstall\Lazarus" : null,
        system ? P(@"Start\Lazarus.lnk") : null,
        () => [P(@"Docs\WindowsPowerShell\Microsoft.PowerShell_profile.ps1"), P(@"Docs\PowerShell\Microsoft.PowerShell_profile.ps1")]);

    string NewData => P(@"AppData\SecondWind");

    static string Journal3 => Features.Journal.Serialize(Enumerable.Range(1, 3).Select(i => new Entry
        { Title = $"w{i}", Exe = @"C:\Apps\a.exe", ClosedAt = DateTimeOffset.Now.AddMinutes(-i * 5) }));

    void FullOldInstall(Migration.Old o)
    {
        Directory.CreateDirectory(o.Data);
        File.WriteAllText(Path.Combine(o.Data, "settings.json"), """{ "schema": 1, "hotkey": "Ctrl+Alt+F12", "language": "ru" }""");
        File.WriteAllText(Path.Combine(o.Data, "journal.json"), Journal3);
        Directory.CreateDirectory(o.Program!);
        File.WriteAllText(Path.Combine(o.Program!, "Lazarus.exe"), "old exe");
        using (var k = Registry.CurrentUser.CreateSubKey(o.RunKey!)) k.SetValue("Lazarus", "\"x\\Lazarus.exe\" --background");
        using (var k = Registry.CurrentUser.CreateSubKey(o.ApprovedKey!)) k.SetValue("Lazarus", new byte[] { 2, 0, 0, 0 });
        using (var k = Registry.CurrentUser.CreateSubKey(o.UninstallKey!)) k.SetValue("DisplayName", "Lazarus");
        Directory.CreateDirectory(Path.GetDirectoryName(o.StartMenuLink!)!);
        File.WriteAllText(o.StartMenuLink!, "lnk");
        var profile = o.Profiles()[0];
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "Import-Module posh-git\r\n\r\n# >>> Lazarus - added by Lazarus\r\n$env:LAZARUS_HOOK = '1'\r\n# <<< Lazarus\r\n");
    }

    [TestMethod]
    public void FullMigration()
    {
        var o = Old();
        FullOldInstall(o);
        var r = Migration.Run(o, NewData, () => throw new AssertFailedException("not running, nothing to ask"));

        Assert.IsTrue(r.Done);
        Assert.IsTrue(r.Settings);
        Assert.AreEqual(3, r.Entries);
        Assert.IsTrue(r.Autostart);
        Assert.AreEqual(1, r.Profiles);
        Assert.HasCount(0, r.Leftovers!);
        StringAssert.Contains(File.ReadAllText(Path.Combine(NewData, "settings.json")), "Ctrl+Alt+F12");
        Assert.HasCount(3, Features.Journal.Parse(File.ReadAllText(Path.Combine(NewData, "journal.json")))!);

        Assert.IsFalse(Directory.Exists(o.Data), "renamed, not left in place");
        Assert.HasCount(1, Directory.GetDirectories(P("AppData"), "Lazarus.migrated-*"));
        Assert.IsTrue(File.Exists(Path.Combine(Directory.GetDirectories(P("AppData"), "Lazarus.migrated-*")[0], "journal.json")), "the old data is kept");
        Assert.IsFalse(Directory.Exists(o.Program));
        Assert.IsNull(Registry.CurrentUser.OpenSubKey(o.RunKey!)?.GetValue("Lazarus"));
        Assert.IsNull(Registry.CurrentUser.OpenSubKey(o.ApprovedKey!)?.GetValue("Lazarus"));
        Assert.IsNull(Registry.CurrentUser.OpenSubKey(o.UninstallKey!));
        Assert.IsFalse(File.Exists(o.StartMenuLink));

        var profile = File.ReadAllText(o.Profiles()[0]);
        Assert.DoesNotContain("# >>> Lazarus", profile);
        Assert.IsTrue(PowerShellHook.Has(profile));
        StringAssert.StartsWith(profile, "Import-Module posh-git\r\n");
        Assert.IsTrue(File.Exists(o.Profiles()[0] + ".bak"));
        Assert.IsFalse(File.Exists(o.Profiles()[1]), "a profile without the old block is not created");
    }

    [TestMethod]
    public void SecondRunFindsNothing()
    {
        var o = Old();
        FullOldInstall(o);
        Migration.Run(o, NewData, () => false);
        var journal = File.ReadAllText(Path.Combine(NewData, "journal.json"));
        var r = Migration.Run(o, NewData, () => false);
        Assert.IsFalse(r.Found);
        Assert.AreEqual(journal, File.ReadAllText(Path.Combine(NewData, "journal.json")));
        Assert.HasCount(1, Directory.GetDirectories(P("AppData"), "Lazarus.migrated-*"));
    }

    [TestMethod]
    public void NoOldInstallation() => Assert.IsFalse(Migration.Run(Old(), NewData, () => false).Found);

    [TestMethod]
    public void PartialOnlyAJournalAndNoProgram()
    {
        var o = Old(system: false);
        Directory.CreateDirectory(o.Data);
        File.WriteAllText(Path.Combine(o.Data, "journal.json"), Journal3);
        var r = Migration.Run(o, NewData, () => false);
        Assert.IsTrue(r.Done);
        Assert.IsFalse(r.Settings);
        Assert.AreEqual(3, r.Entries);
        Assert.IsNull(r.Autostart, "no registry in this run");
        Assert.IsFalse(File.Exists(Path.Combine(NewData, "settings.json")));
        Assert.IsFalse(Directory.Exists(o.Data));
    }

    [TestMethod]
    public void SecondWindOwnFilesWinAndACorruptJournalStaysBehind()
    {
        var o = Old(system: false);
        Directory.CreateDirectory(o.Data);
        File.WriteAllText(Path.Combine(o.Data, "settings.json"), """{ "hotkey": "Ctrl+Alt+F12" }""");
        File.WriteAllText(Path.Combine(o.Data, "journal.json"), "{ \"entries\": [ {");
        Directory.CreateDirectory(NewData);
        File.WriteAllText(Path.Combine(NewData, "settings.json"), """{ "hotkey": "Ctrl+Shift+Win+T" }""");
        var r = Migration.Run(o, NewData, () => false);
        Assert.IsTrue(r.Done);
        Assert.IsFalse(r.Settings, "Second Wind's settings are not overwritten");
        Assert.AreEqual(0, r.Entries);
        StringAssert.Contains(File.ReadAllText(Path.Combine(NewData, "settings.json")), "Ctrl+Shift+Win+T");
        Assert.IsFalse(File.Exists(Path.Combine(NewData, "journal.json")), "a journal that does not read is not copied");
        var aside = Directory.GetDirectories(P("AppData"), "Lazarus.migrated-*")[0];
        Assert.IsTrue(File.Exists(Path.Combine(aside, "journal.json")), "it is still there, in the renamed folder");
    }

    [TestMethod]
    public void RunningLazarusThatTheUserDoesNotCloseLeavesEverything()
    {
        var o = Old();
        FullOldInstall(o);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(true, $@"Local\lojekq.{o.Instance}"); // another "process": a mutex held on another thread
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            var asked = 0;
            var r = Migration.Run(o, NewData, () => { asked++; return false; }, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(1, asked);
            Assert.IsFalse(r.Done);
            Assert.AreEqual("postponed", r.Error);
            Assert.IsTrue(Directory.Exists(o.Data) && Directory.Exists(o.Program) && File.Exists(o.StartMenuLink), "Lazarus left as it was");
            Assert.IsFalse(File.Exists(Path.Combine(NewData, "journal.json")));
        }
        finally { release.Set(); holder.Join(); }
    }
}
