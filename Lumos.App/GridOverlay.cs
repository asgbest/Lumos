using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>Сітка поверх фото — щоб на око перевіряти вертикалі й горизонт під час правки перспективи. Мишу не перехоплює.</summary>
public sealed class GridOverlay : Control
{
    private const int Cells = 8;
    private static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
    private static readonly IPen ShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 3);

    private int _outW = 1, _outH = 1;

    public GridOverlay() => IsHitTestVisible = false;

    public void Update(int outputWidth, int outputHeight)
    {
        (_outW, _outH) = (Math.Max(1, outputWidth), Math.Max(1, outputHeight));
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double scale = Math.Min(Bounds.Width / _outW, Bounds.Height / _outH);
        double w = _outW * scale, h = _outH * scale;
        var r = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        // Квадратні клітинки: крок — за довшою стороною.
        double step = Math.Max(r.Width, r.Height) / Cells;
        foreach (var pen in new[] { ShadowPen, LinePen })
        {
            for (double x = r.Center.X % step; x <= r.Right; x += step)
                if (x >= r.Left) context.DrawLine(pen, new Point(x, r.Top), new Point(x, r.Bottom));
            for (double y = r.Center.Y % step; y <= r.Bottom; y += step)
                if (y >= r.Top) context.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
        }
    }
}
