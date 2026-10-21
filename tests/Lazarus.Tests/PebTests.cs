using System.Diagnostics;
using System.IO;
using Lazarus.Features;

namespace Lazarus.Tests;

/// <summary>Live checks of the undocumented PEB offsets (NativeFacts) on real shells: they fail first when a Windows build moves them.</summary>
[TestClass]
public class PebTests
{
    static Process StartShell(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; // drain, so the shell never blocks on a full pipe
        p.BeginOutputReadLine();
        return p;
    }

    static ShellState Wait(Process p, Func<ShellState, bool> done)
    {
        ShellState? s = null;
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(20); Thread.Sleep(100))
            if ((s = Peb.Read(p.Id)) is not null && done(s)) return s;
        Assert.Fail($"PEB never matched, last: {s}");
        return null!;
    }

    [TestMethod]
    public void CmdCdChangesTheProcessDirectory()
    {
        var dir = Path.GetTempPath().TrimEnd('\\');
        using var p = StartShell("cmd.exe", "/d", "/q", "/k", "cd /d C:\\");
        try
        {
            Wait(p, s => s.Cwd == @"C:\");
            p.StandardInput.WriteLine($"cd /d \"{dir}\"");
            Assert.AreEqual(dir, Wait(p, s => s.Cwd != @"C:\").Cwd, ignoreCase: true);
        }
        finally { p.Kill(); }
    }

    [TestMethod]
    public void EnvironmentVariablesAreRead()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/d /q /k") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        psi.Environment["WT_SESSION"] = "6e2c6f3a-0000-4000-8000-000000000001";
        psi.Environment["WT_PROFILE_ID"] = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}";
        using var p = Process.Start(psi)!;
        try
        {
            var s = Wait(p, s => s.WtSession is not null);
            Assert.AreEqual("6e2c6f3a-0000-4000-8000-000000000001", s.WtSession);
            Assert.AreEqual("{0caa0dad-35be-5f56-a8ff-afceeeaa6101}", s.WtProfile);
        }
        finally { p.Kill(); }
    }

    /// <summary>
    /// The settings one-liner: Set-Location leaves the process directory alone, $env:LAZARUS_CWD lands in the PEB.
    /// pwsh 7 only: Windows PowerShell 5.1 has no LocationChangedAction (NativeFacts).
    /// </summary>
    [TestMethod]
    public void PowerShellHookWritesLazarusCwd()
    {
        if (!File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")) Assert.Inconclusive("no pwsh");
        var dir = Path.GetTempPath().TrimEnd('\\');
        using var p = StartShell("pwsh.exe","-NoLogo", "-NoProfile", "-NonInteractive", "-NoExit", "-Command",
            PowerShellHook.Line + $"; Set-Location -LiteralPath '{dir}'");
        try
        {
            var s = Wait(p, s => s.LazarusCwd is not null);
            Assert.AreEqual(dir, s.LazarusCwd, ignoreCase: true);
            Assert.AreNotEqual(dir, s.Cwd, ignoreCase: true, "Set-Location must not move the process directory");
        }
        finally { p.Kill(); }
    }

    [TestMethod]
    public void ParseEnvironmentStopsAtTheDoubleNull()
    {
        var env = Peb.ParseEnvironment("=C:=C:\\x\0Path=C:\\a;C:\\b\0wt_session=1\0\0garbage=1\0");
        Assert.AreEqual("1", env["WT_SESSION"]);
        Assert.AreEqual(@"C:\a;C:\b", env["PATH"]);
        Assert.HasCount(2, env);
    }
}
