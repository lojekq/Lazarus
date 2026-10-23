using System.Text;
using SecondWind.Features;

namespace SecondWind.Tests;

/// <summary>The SecondWind block in a PowerShell profile: added once at the end, removed alone, the rest of the file byte for byte.</summary>
[TestClass]
public class PowerShellHookTests
{
    public TestContext TestContext { get; set; } = null!;

    const string Theirs = "Import-Module posh-git\r\nfunction prompt { \"[$(Get-Date -f HH:mm)] PS> \" }\r\n";

    string NewProfile()
    {
        var d = Path.Combine(Path.GetTempPath(), "SecondWind.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8], "WindowsPowerShell");
        return Path.Combine(d, "Microsoft.PowerShell_profile.ps1");
    }

    [TestMethod]
    public void AddKeepsTheirPromptAndGoesLast()
    {
        var t = PowerShellHook.Add(Theirs);
        Assert.StartsWith(Theirs.TrimEnd(), t);
        Assert.EndsWith(PowerShellHook.End + "\r\n", t);
        Assert.IsTrue(PowerShellHook.Has(t));
        Assert.AreEqual(Theirs, PowerShellHook.Remove(t), "removed: their text back exactly");
    }

    [TestMethod]
    public void ConnectingAgainLeavesOneBlock()
    {
        var t = PowerShellHook.Add(PowerShellHook.Add(PowerShellHook.Add(Theirs)));
        Assert.AreEqual(1, t.Split(PowerShellHook.Begin).Length - 1);
        Assert.AreEqual(PowerShellHook.Add(Theirs), t);
    }

    [TestMethod]
    public void EmptyProfile()
    {
        Assert.AreEqual(PowerShellHook.Block + "\r\n", PowerShellHook.Add(""));
        Assert.AreEqual("", PowerShellHook.Remove(PowerShellHook.Add("")));
        Assert.AreEqual(Theirs, PowerShellHook.Remove(Theirs), "nothing to remove");
    }

    [TestMethod]
    public void BlockInTheMiddleIsRemovedAlone()
    {
        var t = "a\r\n\r\n" + PowerShellHook.Block + "\r\nb\r\n";
        Assert.AreEqual("a\r\nb\r\n", PowerShellHook.Remove(t));
        Assert.AreEqual("x\r\n" + PowerShellHook.Begin + " damaged", PowerShellHook.Remove("x\r\n" + PowerShellHook.Begin + " damaged"), "no end marker: left alone");
    }

    [TestMethod]
    public void NewFileIsUtf8WithBomAndConnectDisconnectRoundTrips()
    {
        var p = NewProfile();
        PowerShellHook.Edit(p, connect: true);
        var bytes = File.ReadAllBytes(p);
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.IsTrue(PowerShellHook.IsConnected(p));
        Assert.IsFalse(File.Exists(p + ".bak"), "nothing to back up for a new file");
        PowerShellHook.Edit(p, connect: false);
        Assert.IsFalse(PowerShellHook.IsConnected(p));
        Assert.IsTrue(File.Exists(p + ".bak"));
    }

    [TestMethod]
    public void AnsiAndUtf16ProfilesKeepTheirBytes()
    {
        // Windows PowerShell's own Out-File writes UTF-16 LE; Notepad of old wrote ANSI (here CP1251 Cyrillic)
        var ansi = new byte[] { 0x23, 0x20, 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0x0D, 0x0A }; // "# Привет\r\n" in CP1251
        foreach (var original in new[] { ansi, [.. new UnicodeEncoding(false, true).GetPreamble(), .. Encoding.Unicode.GetBytes("# Привет\r\n")] })
        {
            var p = NewProfile();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, original);
            PowerShellHook.Edit(p, connect: true);
            Assert.IsTrue(PowerShellHook.IsConnected(p));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(p + ".bak"), "a .bak of the untouched file");
            PowerShellHook.Edit(p, connect: false);
            var back = File.ReadAllBytes(p);
            CollectionAssert.AreEqual(original[..^2], back[..(original.Length - 2)], "their bytes untouched");
        }
    }
}
