using System.Windows.Threading;
using SecondWind.Features;

namespace SecondWind.Demo;

/// <summary>
/// --demo --autoplay: 8 s on the journal for the window GIF (gif.ps1). The scene GIF with real Explorer and Terminal windows is
/// <see cref="Scene"/> (--autoplay without --demo, in the owner's slot).
/// </summary>
public static class Autoplay
{
    public static void Start(MainWindow w)
    {
        // Seconds after the window is on screen; gif.ps1 starts recording when --rect-file appears, ffmpeg needs ~1 s
        var p = () => JournalPage.Current;
        (double At, Action Do)[] steps =
        [
            (1.6, () => p()?.Type("p")), (1.8, () => p()?.Type("pi")), (2.0, () => p()?.Type("pin")), (2.2, () => p()?.Type("ping")),
            (3.6, () => p()?.Type("")),
            (4.2, () => p()?.Select(1)),
            (5.0, () => p()?.ShowMenu(true)),
            (6.6, () => p()?.ShowMenu(false)),
            (6.8, () => w.Navigate("settings")),
            (8.4, () => w.Navigate("main")),
        ];
        var started = DateTime.UtcNow;
        var next = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            while (next < steps.Length && (DateTime.UtcNow - started).TotalSeconds >= steps[next].At) steps[next++].Do();
            if (next == steps.Length) timer.Stop();
        };
        timer.Start();
    }
}
