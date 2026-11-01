using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>Гістограма R/G/B, як у Lightroom: напівпрозорі канали, що накладаються один на одного.</summary>
public sealed class HistogramView : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush[] ChannelBrushes =
    [
        new SolidColorBrush(Color.FromArgb(150, 235, 70, 70)),
        new SolidColorBrush(Color.FromArgb(150, 80, 210, 90)),
        new SolidColorBrush(Color.FromArgb(150, 80, 130, 240)),
    ];

    private int[]? _counts;

    public void Update(int[] counts)
    {
        _counts = (int[])counts.Clone();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds, 4);
        if (_counts is null) return;

        const int bins = Developer.HistogramBins;
        // Крайні стовпчики (чисто чорне й біле) не беремо в масштаб, інакше решта гістограми стає пласкою.
        int peak = 1;
        for (int c = 0; c < 3; c++)
            for (int i = 1; i < bins - 1; i++)
                peak = Math.Max(peak, _counts[c * bins + i]);

        double w = bounds.Width, h = bounds.Height - 2;
        for (int c = 0; c < 3; c++)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < bins; i++)
                {
                    double v = Math.Min(1, _counts[c * bins + i] / (double)peak);
                    g.LineTo(new Point(i * w / (bins - 1), h - Math.Sqrt(v) * (h - 4)));
                }
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(ChannelBrushes[c], null, geometry);
        }
    }
}
