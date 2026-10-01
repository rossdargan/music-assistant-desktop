using System.Windows;
using System.Windows.Controls;

namespace MaMini.App.Controls;

/// <summary>
/// Hosts content that covers its layout slot without affecting the slot's size, so overlays can't
/// grow a SizeToContent window.
/// </summary>
public sealed class OverlayDecorator : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(0, 0));
        return default;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is { } child)
        {
            // Measure against the real slot so wrapping text lays out at the final width.
            child.Measure(arrangeSize);
            child.Arrange(new Rect(arrangeSize));
        }

        return arrangeSize;
    }
}
