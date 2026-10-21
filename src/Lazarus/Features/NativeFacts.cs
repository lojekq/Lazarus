namespace Lazarus.Features;

/// <summary>
/// Undocumented or unverified Windows facts Lazarus relies on, answered by the stage 1 spike (spec 03 §12)
/// on Windows 11 build 26300, x64, Windows Terminal 1.2x, 2026-10-21. Re-check on every new Windows build (spec 03 §8.2);
/// PebTests re-checks the PEB part on every test run.
/// </summary>
public static class NativeFacts
{
    // PEB (x64). ProcessParameters at 0x20 is from Geoff Chappell; the RTL_USER_PROCESS_PARAMETERS offsets are from phnt
    // and hold on live cmd and pwsh: CurrentDirectory.DosPath (UNICODE_STRING) 0x38, Environment 0x80, EnvironmentSize 0x3F0.
    // `cd` in cmd moves CurrentDirectory; pwsh `$env:X = ...` reaches the block we read; Set-Location leaves CurrentDirectory alone.
    public const int PebProcessParameters = 0x20;
    public const int ParamsCurrentDirectory = 0x38;
    public const int ParamsEnvironment = 0x80;
    public const int ParamsEnvironmentSize = 0x3F0;

    /// <summary>A sane cap for a shell's environment block.</summary>
    public const int MaxEnvironmentBytes = 1 << 20;

    // Windows PowerShell 5.1 has no $ExecutionContext.InvokeCommand.LocationChangedAction ("property not found"):
    // the hook works in pwsh 7 only; a 5.1 tab comes back in its start folder.

    // Elevated processes, seen from a normal one: PROCESS_QUERY_LIMITED_INFORMATION opens them, so the exe path and the command line
    // (NtQueryInformationProcess class 60) are read; the PEB is not (no PROCESS_VM_READ), so no folder and no WT profile.

    /// <summary>
    /// Explorer, Windows 11 tabs: IShellWindows has one entry per tab. IShellBrowser.GetWindow of an entry is its
    /// ShellTabWindowClass child; GA_ROOT of it is the CabinetWClass window. The active tab is the first ShellTabWindowClass child
    /// (FindWindowEx). Calls must come from an STA thread: from an MTA thread IOleWindow.GetWindow fails with
    /// RPC_E_CANTCALLOUT_ININPUTSYNCCALL. Special folders come back as ::{CLSID} and `explorer.exe ::{CLSID}` opens them.
    /// WM_CLOSE to a CabinetWClass window closes only its active tab.
    /// </summary>
    public const string ShellTabClass = "ShellTabWindowClass";

    // Windows Terminal: a tab's shell is a direct child of WindowsTerminal.exe (not of OpenConsole.exe) and carries WT_SESSION
    // and WT_PROFILE_ID in its environment. `wt -p {GUID}` takes the profile GUID; `-d C:\` works when passed through
    // ArgumentList (unquoted); a `;` in a path goes through as `\;`.

    // A classic console window (ConsoleWindowClass) reports the console's client (cmd.exe) as its pid, not conhost.exe.

    // Checked end to end (2026-10-21): `explorer.exe "<path>"` opens paths with spaces, Cyrillic, #, comma, % and ; and a quoted
    // ::{CLSID}; SetWindowPlacement puts a maximized Explorer back on DISPLAY2; WT -p {GUID} -d restores pwsh (hook), cmd (after cd).

    // Not checked: the AUMID of an ApplicationFrameHost (UWP) window through its CoreWindow child; no UWP window was open.
}
