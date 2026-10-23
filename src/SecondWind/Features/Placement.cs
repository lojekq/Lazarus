using System.Drawing;
using SecondWind.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SecondWind.Features;

/// <summary>
/// Window position: read at every tick, put back after a restore. GetWindowPlacement/SetWindowPlacement are used as a pair,
/// so workspace and screen coordinates never mix. SecondWind is PerMonitorV2: all numbers are physical pixels.
/// </summary>
public static unsafe class Placement
{
    public static Place? Read(nint hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!PInvoke.GetWindowPlacement((HWND)hwnd, ref wp) || !PInvoke.GetWindowRect((HWND)hwnd, out var r)) return null;
        var n = wp.rcNormalPosition;
        return new((int)wp.showCmd, [n.left, n.top, n.right, n.bottom], [r.left, r.top, r.right, r.bottom]);
    }

    public static Rectangle? Rect(nint hwnd) =>
        PInvoke.GetWindowRect((HWND)hwnd, out var r) ? Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom) : null;

    public static Rectangle ToRect(int[] ltrb) => Rectangle.FromLTRB(ltrb[0], ltrb[1], ltrb[2], ltrb[3]);

    /// <summary>
    /// How much the window already sits where it was: intersection over union of the two rectangles, 0..1.
    /// ≥ 0.8 → the program put itself back, don't touch it.
    /// </summary>
    public static double Match(Rectangle saved, Rectangle now)
    {
        var i = Rectangle.Intersect(saved, now);
        if (i.IsEmpty) return 0;
        double inter = (double)i.Width * i.Height, union = (double)saved.Width * saved.Height + (double)now.Width * now.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public const double InPlace = 0.8;

    /// <summary>
    /// Back to the saved place: the normal rectangle clamped to today's monitors, the saved show state (minimized comes back normal).
    /// ponytail: workspace coordinates are treated as screen ones for the clamp; they differ only by a top or left taskbar,
    /// and SetWindowPlacement keeps the window on screen by itself
    /// </summary>
    public static bool Apply(nint hwnd, Place place)
    {
        var normal = Monitors.ClampToVisible(ToRect(place.Normal), Monitors.Snapshot());
        var wp = new WINDOWPLACEMENT
        {
            length = (uint)sizeof(WINDOWPLACEMENT),
            showCmd = place.ShowCmd == (int)SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED : SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            rcNormalPosition = new RECT(normal.Left, normal.Top, normal.Right, normal.Bottom),
        };
        return PInvoke.SetWindowPlacement((HWND)hwnd, in wp);
    }
}
