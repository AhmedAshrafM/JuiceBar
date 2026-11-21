using System.Windows.Automation;
using Bfm.Core;

namespace Bfm.Interop;

/// <summary>
/// Reads where everything on the Windows 11 taskbar sits (Widgets/weather, Start, search, Task View,
/// pinned and running apps, the tray) through UI Automation, so the bar can sit in free space instead
/// of over them. These items have no windows of their own; they only exist in the taskbar's XAML tree.
/// Call off the UI thread: it is a cross-process query into Explorer.
/// </summary>
internal static class TaskbarLayout
{
    static readonly Condition Buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

    /// <summary>Screen rectangles of every button on the taskbar, or null if Explorer couldn't be queried.</summary>
    public static List<Rectangle>? Occupied(IntPtr trayHwnd)
    {
        try
        {
            var tray = AutomationElement.FromHandle(trayHwnd);
            var items = new List<Rectangle>();
            foreach (AutomationElement button in tray.FindAll(TreeScope.Descendants, Buttons))
            {
                var r = button.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                items.Add(Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom)));
            }
            items.Sort((a, b) => a.Left.CompareTo(b.Left));
            return items;
        }
        catch (Exception ex)
        {
            Log.Write("taskbar layout: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Picks the bar's left edge: the spot closest to <paramref name="want"/> where a bar of
    /// <paramref name="width"/> fits between taskbar items. Null when no gap is wide enough.
    /// </summary>
    public static int? Place(Rectangle taskbar, IReadOnlyList<Rectangle> items, int width, int pad, int offset)
    {
        var gaps = new List<(int L, int R)> { (taskbar.Left + pad, taskbar.Right - pad) };
        foreach (var item in items)
        {
            if (item.Bottom <= taskbar.Top || item.Top >= taskbar.Bottom) continue; // another monitor's taskbar
            int cutL = item.Left - pad, cutR = item.Right + pad;
            var next = new List<(int L, int R)>();
            foreach (var g in gaps)
            {
                if (cutR <= g.L || cutL >= g.R) { next.Add(g); continue; }
                if (cutL > g.L) next.Add((g.L, cutL));
                if (cutR < g.R) next.Add((cutR, g.R));
            }
            gaps = next;
        }
        if (gaps.Count == 0) return null;

        // The offset counts from the first free spot, so 0 means "right after the leftmost item".
        int want = gaps[0].L + offset;
        int? best = null;
        int bestDistance = int.MaxValue;
        foreach (var g in gaps)
        {
            if (g.R - g.L < width) continue;
            int x = Math.Clamp(want, g.L, g.R - width);
            int distance = Math.Abs(x - want);
            if (distance < bestDistance)
            {
                best = x;
                bestDistance = distance;
            }
        }
        return best;
    }
}
