using System.Diagnostics;
using System.Drawing;
using System.IO;
using Lazarus.Features;
using Lazarus.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Lazarus.Demo;

/// <summary>
/// --autoplay without --demo: the scene GIF (spec 03 §10), the one place where Lazarus runs for real in a recording.
/// Opens C:\Demo\Projects\pingwhen in Explorer on the right half of the recorded area, closes it, restores it; then a Windows Terminal
/// with three tabs, closes it, restores it. tools/scene.ps1 records the area; only in the owner's slot (00-shared §9.6).
/// </summary>
public static class Scene
{
    const string Root = @"C:\Demo\Projects";

    public static async Task Run(Rectangle area)
    {
        try
        {
            foreach (var d in new[] { "pingwhen", "mlink", "vram-bouncer" }) Directory.CreateDirectory(Path.Combine(Root, d));
            var mine = TopWindows.List().Select(w => w.Hwnd).ToHashSet(); // anything newer was opened by the scene
            await Task.Delay(1500); // the tracker's first look at the desktop

            var explorer = await Open("CabinetWClass", "explorer.exe", [Path.Combine(Root, "pingwhen")]);
            Move(explorer, new(area.Left + area.Width / 2, area.Top, area.Width / 2, area.Height));
            await Task.Delay(4000); // older than WindowFilter.MinAge
            Close(explorer);
            await Task.Delay(1200);
            Engine.RestoreLast();
            await Task.Delay(3000);

            var terminal = await Open("CASCADIA_HOSTING_WINDOW_CLASS", "wt.exe",
                ["-w", "new", "nt", "-d", Path.Combine(Root, "pingwhen"), ";", "nt", "-d", Path.Combine(Root, "mlink"), ";", "nt", "-d", Path.Combine(Root, "vram-bouncer")]);
            Move(terminal, new(area.Left + 24, area.Top + 40, area.Width * 11 / 20, area.Height * 3 / 4));
            await Task.Delay(4500);
            Close(terminal);
            await Task.Delay(700);
            if (PInvoke.IsWindow((HWND)terminal) && PInvoke.GetForegroundWindow() == (HWND)terminal) Enter(); // "Close all tabs?"
            await Task.Delay(4500); // the tracker waits 3 s for the tabs; before that it would say "still closing"
            Engine.RestoreLast();

            // After the take: close what the scene opened, so the next take starts from the same desktop
            await Task.Delay(9000);
            foreach (var w in TopWindows.List().Where(w => !mine.Contains(w.Hwnd) && w.Class is "CabinetWClass" or "CASCADIA_HOSTING_WINDOW_CLASS"))
            {
                PInvoke.SetForegroundWindow((HWND)w.Hwnd);
                Close(w.Hwnd);
                await Task.Delay(700);
                if (PInvoke.IsWindow((HWND)w.Hwnd) && PInvoke.GetForegroundWindow() == (HWND)w.Hwnd) Enter();
            }
        }
        catch (Exception e) { Log.Error("scene", e); }
    }

    static async Task<nint> Open(string cls, string exe, string[] args)
    {
        var before = TopWindows.List().Where(w => w.Class == cls).Select(w => w.Hwnd).ToHashSet();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(15); await Task.Delay(200))
            if (TopWindows.List().FirstOrDefault(w => w.Class == cls && !before.Contains(w.Hwnd)) is { } w) return w.Hwnd;
        throw new TimeoutException(cls);
    }

    static void Move(nint hwnd, Rectangle r) =>
        PInvoke.SetWindowPos((HWND)hwnd, HWND.Null, r.Left, r.Top, r.Width, r.Height, SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

    static void Close(nint hwnd) => PInvoke.PostMessage((HWND)hwnd, 0x0010, 0, 0); // WM_CLOSE

    static void Enter()
    {
        const byte VK_RETURN = 0x0D;
        PInvoke.keybd_event(VK_RETURN, 0, 0, 0);
        PInvoke.keybd_event(VK_RETURN, 0, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
    }
}
