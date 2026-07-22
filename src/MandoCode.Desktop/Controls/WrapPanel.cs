using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// Left-to-right flow layout that wraps to the next line only when a child won't fit the
/// remaining width. WinUI 3 ships no wrap panel for variable-width children
/// (VariableSizedWrapGrid is uniform-cell), so the approval bar's option buttons — which
/// should read horizontally but must survive narrow windows and long labels — use this.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, maxWidth = 0, totalHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (lineWidth > 0 && lineWidth + HorizontalSpacing + d.Width > availableSize.Width)
            {
                maxWidth = Math.Max(maxWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = d.Width;
                lineHeight = d.Height;
            }
            else
            {
                lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + d.Width;
                lineHeight = Math.Max(lineHeight, d.Height);
            }
        }
        maxWidth = Math.Max(maxWidth, lineWidth);
        totalHeight += lineHeight;
        return new Size(
            double.IsInfinity(availableSize.Width) ? maxWidth : Math.Min(maxWidth, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + d.Width > finalSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            if (x > 0) x += HorizontalSpacing;
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width;
            lineHeight = Math.Max(lineHeight, d.Height);
        }
        return finalSize;
    }
}
