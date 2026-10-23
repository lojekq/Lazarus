using System.IO;
using System.Text;
using SecondWind.Shell;

namespace SecondWind.Features;

/// <summary>
/// Set-Location never moves a PowerShell process's own folder, the one <see cref="Peb"/> reads. The hook wraps the prompt
/// function so that before each prompt [Environment]::CurrentDirectory follows the current FileSystem location, in Windows
/// PowerShell 5.1 and pwsh 7 alike, keeping the user's prompt. SecondWind writes it as a marked block at the end of the
/// CurrentUserCurrentHost profile of each version (Settings → PowerShell folder → Connect) and removes only that block.
/// $env:SECONDWIND_HOOK marks a shell that runs it; LAZARUS_HOOK and LAZARUS_CWD of the Lazarus versions are still read.
/// </summary>
public static class PowerShellHook
{
    public const string Begin = "# >>> SecondWind", End = "# <<< SecondWind";

    /// <summary>The block as Lazarus (this app's old name, 1.0.2) wrote it; Migration swaps it for the new one.</summary>
    public const string OldBegin = "# >>> Lazarus", OldEnd = "# <<< Lazarus";

    /// <summary>ASCII only: Windows PowerShell reads a profile without a BOM in the ANSI code page.</summary>
    public static readonly string Block = string.Join("\r\n",
        Begin + " - added by Second Wind; Settings > PowerShell folder > Disconnect removes it",
        "$env:SECONDWIND_HOOK = '1'",
        "if (-not (Test-Path Variable:global:__SecondWindPrompt)) {",
        "    $global:__SecondWindPrompt = $function:prompt",
        "    function global:prompt {",
        "        $p = & $global:__SecondWindPrompt",
        "        $l = Get-Location -PSProvider FileSystem -ErrorAction SilentlyContinue",
        "        if ($l) { [Environment]::CurrentDirectory = $l.ProviderPath }",
        "        $p",
        "    }",
        "}",
        End);

    public static bool Has(string text) => text.Contains(Begin) && text.Contains(End);

    /// <summary>The profile with the block at its end, once (an older block is replaced).</summary>
    public static string Add(string text)
    {
        var rest = Remove(text).TrimEnd('\r', '\n');
        return (rest.Length > 0 ? rest + "\r\n\r\n" : "") + Block + "\r\n";
    }

    /// <summary>The profile without any block between these markers (ours by default) and without the blank line <see cref="Add"/> put before it.</summary>
    public static string Remove(string text, string begin = Begin, string end = End)
    {
        for (int b; (b = text.IndexOf(begin, StringComparison.Ordinal)) >= 0;)
        {
            var e = text.IndexOf(end, b, StringComparison.Ordinal);
            if (e < 0) break; // a damaged block: leave the file as it is
            e += end.Length;
            if (text.AsSpan(e).StartsWith("\r\n")) e += 2; else if (text.AsSpan(e).StartsWith("\n")) e++;
            var start = b;
            if (text[..start].EndsWith("\r\n\r\n")) start -= 2; else if (text[..start].EndsWith("\n\n")) start--;
            text = text[..start] + text[e..];
        }
        return text;
    }

    // ---------------------------------------------------------------- profile files

    /// <summary>
    /// Adds or removes the block in one profile file, keeping its encoding byte for byte: a BOM file stays in its encoding,
    /// a file without one is edited as Latin-1 (every byte round-trips; the block is ASCII). A changed file gets a .bak first.
    /// A new file is UTF-8 with a BOM, which both versions read.
    /// </summary>
    public static void Edit(string profile, bool connect) => Change(profile, connect ? Add : t => Remove(t), create: connect);

    /// <summary>
    /// Lazarus' block (this app's old name) becomes this one, in its place at the end (Migration). A profile without it is left
    /// alone. True when the profile had it.
    /// </summary>
    public static bool ReplaceOld(string profile)
    {
        if (!File.Exists(profile) || !Read(profile).Text.Contains(OldBegin, StringComparison.Ordinal)) return false;
        Change(profile, t => Add(Remove(t, OldBegin, OldEnd)), create: false);
        return true;
    }

    static void Change(string profile, Func<string, string> change, bool create)
    {
        var exists = File.Exists(profile);
        if (!exists && !create) return;
        var (text, encoding, bom) = exists ? Read(profile) : ("", new UTF8Encoding(false), new UTF8Encoding(true).GetPreamble());
        var changed = change(text);
        if (exists && changed == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        if (exists) File.Copy(profile, profile + ".bak", overwrite: true);
        var tmp = profile + ".secondwind-tmp";
        File.WriteAllBytes(tmp, [.. bom, .. encoding.GetBytes(changed)]);
        File.Move(tmp, profile, overwrite: true);
    }

    internal static (string Text, Encoding Encoding, byte[] Bom) Read(string profile)
    {
        var bytes = File.ReadAllBytes(profile);
        foreach (var e in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
        {
            var bom = e.GetPreamble();
            if (bytes.AsSpan().StartsWith(bom)) return (e.GetString(bytes, bom.Length, bytes.Length - bom.Length), e, bom);
        }
        return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, []);
    }

    public static bool IsConnected(string profile) => File.Exists(profile) && Has(Read(profile).Text);

    // ---------------------------------------------------------------- the two PowerShells

    /// <summary>
    /// One installed PowerShell: its CurrentUserCurrentHost profile as PowerShell itself reports it (Documents may be moved to
    /// OneDrive) and its effective execution policy. Restricted and AllSigned never load an unsigned profile.
    /// </summary>
    public sealed record Host(string Name, string Profile, string Policy)
    {
        public bool PolicyBlocks => Policy is "Restricted" or "AllSigned" or "Undefined";
    }

    static readonly (string Name, string Exe, string Folder)[] Versions =
    [
        ("Windows PowerShell 5.1", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), "WindowsPowerShell"),
        ("PowerShell 7", "pwsh.exe", "PowerShell"),
    ];

    /// <summary>Asks each installed PowerShell (a second or two each, in parallel); the missing ones are left out.</summary>
    public static async Task<List<Host>> FindAsync()
    {
        var found = await Task.WhenAll(Versions.Select(v => Task.Run(() => Query(v.Name, v.Exe))));
        return found.OfType<Host>().ToList();
    }

    static Host? Query(string name, string exe)
    {
        try
        {
            // The path goes out as Base64: ASCII whatever the console code page (a Cyrillic user name). Windows PowerShell started
            // with a pwsh 7 PSModulePath (SecondWind run from a pwsh window) can't load its own Get-ExecutionPolicy: it gets the machine path
            var reset = exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                ? "$env:PSModulePath = [Environment]::GetEnvironmentVariable('PSModulePath', 'Machine'); " : "";
            var (code, output) = ConsoleTool.Run(exe,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                 reset + "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($PROFILE.CurrentUserCurrentHost)); [string](Get-ExecutionPolicy)"],
                TimeSpan.FromSeconds(20), Encoding.ASCII);
            var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (code != 0 || lines.Length < 2) { Log.Warn($"{name}: profile query gave exit {code}, {lines.Length} lines"); return null; }
            return new(name, Encoding.UTF8.GetString(Convert.FromBase64String(lines[0])), lines[1]);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException or FormatException) { return null; } // not installed, hung
    }

    /// <summary>Where the profiles are if PowerShell can't be asked: Documents (known folder, moved or not).</summary>
    static IEnumerable<string> DefaultProfiles() =>
        Versions.Select(v => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), v.Folder, "Microsoft.PowerShell_profile.ps1"));

    /// <summary>For uninstall: the block leaves every profile it may be in. Never throws.</summary>
    public static void DisconnectAll()
    {
        List<string> profiles = [.. DefaultProfiles()];
        try { profiles.AddRange(FindAsync().GetAwaiter().GetResult().Select(h => h.Profile)); } catch { }
        foreach (var p in profiles.Distinct(StringComparer.OrdinalIgnoreCase))
            try { Edit(p, connect: false); }
            catch (Exception e) { Log.Warn("PowerShell profile not cleaned", e); }
    }
}
