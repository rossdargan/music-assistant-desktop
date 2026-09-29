using MaMini.Core.Settings;

namespace MaMini.Core.Geometry;

/// <summary>Integer rectangle in physical pixels.</summary>
public readonly record struct IntRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public static IntRect FromLTRB(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public IntRect Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    public IntRect MoveTo(int left, int top) => this with { Left = left, Top = top };

    public int IntersectionArea(IntRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>Pure positioning maths for the widget (snapping, clamping, defaults).</summary>
public static class SnapMath
{
    /// <summary>
    /// Snaps edges of <paramref name="window"/> that are within <paramref name="threshold"/> pixels of the
    /// matching work-area edges.
    /// </summary>
    public static IntRect Snap(IntRect window, IntRect workArea, int threshold)
    {
        var left = window.Left;
        var top = window.Top;

        if (Math.Abs(window.Left - workArea.Left) <= threshold)
        {
            left = workArea.Left;
        }
        else if (Math.Abs(window.Right - workArea.Right) <= threshold)
        {
            left = workArea.Right - window.Width;
        }

        if (Math.Abs(window.Top - workArea.Top) <= threshold)
        {
            top = workArea.Top;
        }
        else if (Math.Abs(window.Bottom - workArea.Bottom) <= threshold)
        {
            top = workArea.Bottom - window.Height;
        }

        return window.MoveTo(left, top);
    }

    /// <summary>Moves <paramref name="window"/> so it lies fully inside <paramref name="workArea"/> where possible.</summary>
    public static IntRect ClampInto(IntRect window, IntRect workArea)
    {
        var left = Math.Max(workArea.Left, Math.Min(window.Left, workArea.Right - window.Width));
        var top = Math.Max(workArea.Top, Math.Min(window.Top, workArea.Bottom - window.Height));
        return window.MoveTo(left, top);
    }

    /// <summary>
    /// Returns a position that is visible on one of <paramref name="workAreas"/>: clamps into the work area the
    /// window overlaps most, or places it in <paramref name="fallbackCorner"/> of the first (primary) work area
    /// when it's not on any screen (e.g. a monitor was unplugged).
    /// </summary>
    public static IntRect EnsureVisible(IntRect window, IReadOnlyList<IntRect> workAreas, WidgetCorner fallbackCorner, int margin)
    {
        if (workAreas.Count == 0)
        {
            return window;
        }

        var best = workAreas.MaxBy(a => a.IntersectionArea(window));
        var overlap = best.IntersectionArea(window);
        var minVisible = Math.Min(window.Width, 40) * Math.Min(window.Height, 20);
        if (overlap >= minVisible)
        {
            return ClampInto(window, best);
        }

        return DefaultPosition(window.Width, window.Height, workAreas[0], fallbackCorner, margin);
    }

    public static IntRect DefaultPosition(int width, int height, IntRect workArea, WidgetCorner corner, int margin)
    {
        var left = corner is WidgetCorner.BottomLeft or WidgetCorner.TopLeft
            ? workArea.Left + margin
            : workArea.Right - width - margin;
        var top = corner is WidgetCorner.TopLeft or WidgetCorner.TopRight
            ? workArea.Top + margin
            : workArea.Bottom - height - margin;
        return ClampInto(new IntRect(left, top, width, height), workArea);
    }

    /// <summary>True when the window's centre is in the right half of the work area (used to anchor compact mode).</summary>
    public static bool IsOnRightHalf(IntRect window, IntRect workArea) =>
        window.Left + (window.Width / 2) > workArea.Left + (workArea.Width / 2);
}
