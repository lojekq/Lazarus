using SecondWind.Features;
using SecondWind.Shell;

namespace SecondWind.Demo;

/// <summary>
/// --demo: a made-up journal for screenshots and GIFs (spec 03 §10). The user is "demo", projects live in D:\Projects; nothing real.
/// Under --demo no real services start, nothing is written, no hotkeys, no update check (00-shared §2.3).
/// </summary>
public static class DemoData
{
    const string Pwsh = "{574e775e-4f2a-5b96-ac1e-a2962a402336}";
    const string Cmd = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}";

    public static void Fill(Journal j)
    {
        var now = DateTimeOffset.Now;
        Place P(int l, int t, int r, int b, int show = 1) => new(show, [l, t, r, b], [l, t, r, b]);
        Entry[] entries =
        [
            new() { ClosedAt = now.AddSeconds(-20), Kind = EntryKind.Explorer, Title = "pingwhen", Exe = @"C:\Windows\explorer.exe",
                Paths = [@"D:\Projects\pingwhen", @"D:\Projects\pingwhen\src", @"D:\Projects\pingwhen\docs"], Active = @"D:\Projects\pingwhen",
                Placement = P(1720, 0, 3440, 1408), Monitor = "DISPLAY1" },
            new() { ClosedAt = now.AddMinutes(-2), Kind = EntryKind.Terminal, Title = "pingwhen", Exe = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe",
                Tabs = [new("pwsh.exe", Pwsh, @"D:\Projects\pingwhen"), new("pwsh.exe", Pwsh, @"D:\Projects\MLink"), new("cmd.exe", Cmd, @"C:\Users\demo")],
                Placement = P(3448, 8, 4872, 1272), Monitor = "DISPLAY2" },
            new() { ClosedAt = now.AddMinutes(-5), Kind = EntryKind.Explorer, Title = "src", Exe = @"C:\Windows\explorer.exe",
                Paths = [@"D:\Projects\pingwhen\src"], Active = @"D:\Projects\pingwhen\src", TabOf = 0x10A2B4,
                Placement = P(1720, 0, 3440, 1408), Monitor = "DISPLAY1" },
            new() { ClosedAt = now.AddMinutes(-7), Kind = EntryKind.App, Title = "vram-bouncer - Visual Studio Code", Exe = @"C:\Users\demo\AppData\Local\Programs\Microsoft VS Code\Code.exe",
                CommandLine = @"""C:\Users\demo\AppData\Local\Programs\Microsoft VS Code\Code.exe"" D:\Projects\vram-bouncer", WorkDir = @"C:\Users\demo\AppData\Local\Programs\Microsoft VS Code",
                Placement = P(0, 0, 1720, 1408, 3), Monitor = "DISPLAY1" },
            new() { ClosedAt = now.AddMinutes(-16), Kind = EntryKind.App, Title = "OBS Studio", Exe = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                CommandLine = @"""C:\Program Files\obs-studio\bin\64bit\obs64.exe"" --minimize-to-tray", WorkDir = @"C:\Program Files\obs-studio\bin\64bit",
                Placement = P(3500, 1300, 4800, 2400), Monitor = "DISPLAY2" },
            new() { ClosedAt = now.AddMinutes(-24), Kind = EntryKind.Packaged, Title = Loc.T("Demo.Notepad"), Exe = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad\Notepad\Notepad.exe",
                Aumid = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", Placement = P(900, 300, 2100, 1100), Monitor = "DISPLAY1" },
            new() { ClosedAt = now.AddMinutes(-41), Kind = EntryKind.Group, Items = // Explorer restarted: three windows at once
            [
                Folder("MLink", @"D:\Projects\MLink", now.AddMinutes(-41)),
                Folder("2026", @"D:\Photos\2026", now.AddMinutes(-41).AddSeconds(-0.4)),
                Folder(Loc.T("Demo.Downloads"), @"C:\Users\demo\Downloads", now.AddMinutes(-41).AddSeconds(-0.8)),
            ] },
            new() { ClosedAt = now.AddMinutes(-58), Kind = EntryKind.Console, Title = Loc.T("Demo.Admin"), Exe = @"C:\Windows\System32\cmd.exe",
                CommandLine = @"""C:\Windows\System32\cmd.exe""", WorkDir = @"C:\Windows\System32", Elevated = true,
                Placement = P(200, 200, 1400, 900), Monitor = "DISPLAY1" },
            new() { ClosedAt = now.AddMinutes(-85), Kind = EntryKind.Console, Title = "python train.py", Exe = @"C:\Users\demo\AppData\Local\Programs\Python\Python313\python.exe",
                CommandLine = "python train.py --epochs 20", WorkDir = @"D:\ml\llm-tune",
                Placement = P(3460, 100, 4700, 900), Monitor = "DISPLAY2" },
        ];
        foreach (var e in entries.Reverse()) j.Push(e);

        Entry Folder(string title, string path, DateTimeOffset at) => new()
        {
            ClosedAt = at, Kind = EntryKind.Explorer, Title = title, Exe = @"C:\Windows\explorer.exe", Paths = [path], Active = path,
            Placement = P(600, 200, 1900, 1000), Monitor = "DISPLAY1",
        };
    }

    /// <summary>The python entry, for the confirmation dialog screenshot.</summary>
    public static Entry Confirm(Journal j) => j.Entries.First(e => e.Kind == EntryKind.Console && !e.Elevated);
}
