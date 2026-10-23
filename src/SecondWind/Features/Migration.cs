using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SecondWind.Shell;

namespace SecondWind.Features;

/// <summary>
/// Second Wind was Lazarus until 1.0.2. The installed copy takes over an old installation once, at its start: it closes the running
/// Lazarus (its exit channel, or asks the user to close it from the tray), copies settings and the journal (each one read back
/// before it counts), swaps the PowerShell profile block for the new one (.bak first), removes Lazarus' autostart, shortcut,
/// Apps entry and program folder, and renames the old data folder to Lazarus.migrated-&lt;date&gt; instead of deleting it.
/// Idempotent: nothing old left = nothing to do; Second Wind's own files are never overwritten. A failure before the copy is done
/// leaves Lazarus as it was.
/// </summary>
public static class Migration
{
    const string OldName = "Lazarus";

    /// <summary>
    /// Where an old installation lives. Registry keys and paths that are null are not touched: a test run
    /// (LAZARUS_DATA_DIR) has only the data folder and a sandbox for the PowerShell profiles.
    /// </summary>
    public sealed record Old(
        string Data, string Local, string? Program, string Instance,
        string? RunKey, string? ApprovedKey, string? UninstallKey, string? StartMenuLink,
        Func<IReadOnlyList<string>> Profiles);

    public sealed record Result(bool Found, bool Done = false, bool Settings = false, int Entries = 0, bool? Autostart = null,
        int Profiles = 0, List<string>? Leftovers = null, string? Error = null);

    /// <summary>What happened at this start, for the toast once the tray is up.</summary>
    public static Result? Last { get; private set; }

    /// <summary>
    /// Program.Main, before the main settings load: closes Lazarus (asks with a plain message box: no WPF yet), moves its data and
    /// reports a failure at once. True when settings came over, so the caller loads them (and the language) again.
    /// </summary>
    public static bool AtStart()
    {
        if (Locate() is not { } old) return false;
        Result r;
        try { r = Run(old, AppInfo.DataDir, () => System.Windows.MessageBox.Show(string.Format(Loc.T("Migrate.Close"), AppInfo.DisplayName),
            AppInfo.Title, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.OK); }
        catch (Exception e) { Log.Error("migration", e); r = new(true, Error: e.Message); }
        if (!r.Found) return false;
        Last = r;
        if (r.Error is { } error)
            System.Windows.MessageBox.Show(string.Format(Loc.T(error == "postponed" ? "Migrate.Postponed" : "Migrate.Failed"), AppInfo.DisplayName, error),
                AppInfo.Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        if (r.Autostart is { } on) App.AutostartOnInstall = on;
        return r.Settings;
    }

    /// <summary>"Moved over from Lazarus: …" for the tray balloon; null when there is nothing to say.</summary>
    public static string? Message => Last is { Done: true } r
        ? string.Format(Loc.T("Migrate.Done"), r.Entries) + (r.Leftovers is { Count: > 0 } l ? " " + string.Format(Loc.T("Migrate.Left"), string.Join(", ", l)) : "")
        : null;

    /// <summary>The installed copy looks for the real Lazarus; a dev instance only for a test one named by LAZARUS_DATA_DIR.</summary>
    public static Old? Locate()
    {
        var a = Args.Current;
        if (a.Demo || a.Screenshot is not null || a.Autoplay) return null;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (AppInfo.IsDev)
        {
            if (Environment.GetEnvironmentVariable("LAZARUS_DATA_DIR") is not { Length: > 0 } dir) return null;
            var (suffix, data, localDir) = AppInfo.Resolve(true, dir, appData, local); // the hash a dev Lazarus gave its mutex
            var sandbox = Path.Combine(AppInfo.DataDir, "profiles");
            return new(data, localDir, null, OldName + suffix, null, null, null, null,
                () => [Path.Combine(sandbox, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"), Path.Combine(sandbox, "PowerShell", "Microsoft.PowerShell_profile.ps1")]);
        }
        if (!Install.IsInstalled) return null; // a portable copy leaves an installed Lazarus alone
        return new(Path.Combine(appData, OldName), Path.Combine(local, OldName), Path.Combine(local, "Programs", OldName), OldName,
            @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), OldName + ".lnk"),
            () =>
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                List<string> p = [Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"), Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1")];
                try { p.AddRange(PowerShellHook.FindAsync().GetAwaiter().GetResult().Select(h => h.Profile)); } catch { }
                return p.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
    }

    /// <param name="newData">Second Wind's data folder (AppInfo.DataDir)</param>
    /// <param name="askToClose">Lazarus did not exit by itself: true = the user closed it, look again; false = later</param>
    public static Result Run(Old old, string newData, Func<bool> askToClose, TimeSpan? wait = null)
    {
        if (!Found(old)) return new(false);

        // 1. Lazarus 1.0.1+ exits on its .exit event; 1.0.0 or a hung one needs the user
        while (Running(old.Instance))
        {
            if (EventWaitHandle.TryOpenExisting($@"Local\lojekq.{old.Instance}.exit", out var exit))
                using (exit) exit.Set();
            if (WaitFree(old.Instance, wait ?? TimeSpan.FromSeconds(10))) break;
            if (!askToClose()) return new(true, Error: "postponed");
        }

        // 2. Settings and journal, each read back from its new place before it counts. Second Wind's own files win.
        bool settings;
        int entries;
        try
        {
            Directory.CreateDirectory(newData);
            settings = Copy(Path.Combine(old.Data, "settings.json"), Path.Combine(newData, "settings.json"), t => JsonSerializer.Deserialize<JsonElement>(t).ValueKind == JsonValueKind.Object ? 1 : null) is not null;
            entries = Copy(Path.Combine(old.Data, "journal.json"), Path.Combine(newData, "journal.json"), t => Journal.Parse(t)?.Count) ?? 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("migration: copy failed", e);
            return new(true, Error: e.Message); // nothing of Lazarus was touched yet
        }

        // 3. Autostart carries over: on if Lazarus had it
        bool? autostart = old.RunKey is null ? null : Registry.CurrentUser.OpenSubKey(old.RunKey)?.GetValue(OldName) is not null;

        // 4. The PowerShell block: Lazarus' becomes ours, in place
        var profiles = 0;
        foreach (var p in old.Profiles())
            try { if (PowerShellHook.ReplaceOld(p)) profiles++; }
            catch (Exception e) { Log.Warn("migration: a PowerShell profile kept the old block", e); }

        // 5. Lazarus' own entries in the system, then its program folder; the data folder is renamed, never deleted
        var left = new List<string>();
        void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { left.Add(what); Log.Warn($"migration: {what} left", e); }
        }
        if (old.RunKey is not null) Try("autostart", () => Registry.CurrentUser.OpenSubKey(old.RunKey, true)?.DeleteValue(OldName, false));
        if (old.ApprovedKey is not null) Try("autostart", () => Registry.CurrentUser.OpenSubKey(old.ApprovedKey, true)?.DeleteValue(OldName, false));
        if (old.UninstallKey is not null) Try("Apps entry", () => Registry.CurrentUser.DeleteSubKeyTree(old.UninstallKey, false));
        if (old.StartMenuLink is not null) Try(old.StartMenuLink, () => File.Delete(old.StartMenuLink));
        if (old.Program is not null) Try(old.Program, () => DeleteFolder(old.Program));
        foreach (var dir in new[] { old.Data, old.Local })
            if (Directory.Exists(dir)) Try(dir, () => Directory.Move(dir, Aside(dir)));

        Log.Info($"migration: settings {settings}, {entries} entries, {profiles} profile(s), {left.Count} left");
        return new(true, true, settings, entries, autostart, profiles, left);
    }

    static bool Found(Old old) =>
        Directory.Exists(old.Data) || old.Program is not null && Directory.Exists(old.Program)
        || old.UninstallKey is not null && Registry.CurrentUser.OpenSubKey(old.UninstallKey) is { } k && Dispose(k);

    static bool Dispose(IDisposable d) { d.Dispose(); return true; }

    static bool Running(string instance)
    {
        if (!Mutex.TryOpenExisting($@"Local\lojekq.{instance}", out var m)) return false;
        m.Dispose();
        return !WaitFree(instance, TimeSpan.Zero);
    }

    static bool WaitFree(string instance, TimeSpan timeout)
    {
        using var m = new Mutex(false, $@"Local\lojekq.{instance}");
        try { if (!m.WaitOne(timeout)) return false; }
        catch (AbandonedMutexException) { } // it ended without letting go: ours now
        m.ReleaseMutex();
        return true;
    }

    /// <summary>Copies src to dst unless dst exists, through a temp file, and returns what <paramref name="read"/> finds in the copy; null = not copied.</summary>
    static int? Copy(string src, string dst, Func<string, int?> read)
    {
        if (!File.Exists(src) || File.Exists(dst)) return null;
        int? ok;
        try { ok = read(File.ReadAllText(src)); } catch (JsonException) { ok = null; }
        if (ok is null) { Log.Warn($"migration: {Path.GetFileName(src)} does not read, left in the old folder"); return null; }
        var tmp = dst + ".migrating";
        File.Copy(src, tmp, overwrite: true);
        if (read(File.ReadAllText(tmp)) != ok) { File.Delete(tmp); throw new IOException($"{Path.GetFileName(dst)} did not copy intact"); }
        File.Move(tmp, dst);
        return ok;
    }

    /// <summary>The program folder of a Lazarus that has just exited may stay locked for a moment.</summary>
    static void DeleteFolder(string dir)
    {
        for (var attempt = 1; Directory.Exists(dir); attempt++)
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (attempt < 10 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
    }

    static string Aside(string dir)
    {
        var target = $"{dir}.migrated-{DateTime.Now:yyyyMMdd}";
        return Directory.Exists(target) ? $"{target}-{DateTime.Now:HHmmss}" : target;
    }
}
