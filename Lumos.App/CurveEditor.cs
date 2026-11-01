using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Редактор точкової кривої, як у Lightroom: клік — нова точка, тягнути — рухати,
/// подвійний клік або витягнути точку за межі квадрата — видалити. Крайні точки не видаляються (це точки чорного й білого).
/// Позаду — гістограма, щоб було видно, які тони зачіпає точка.
/// </summary>
public sealed class CurveEditor : Control
{
    private const double Pad = 6, HitRadius = 10, RemoveDistance = 28, MinGap = 0.01;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush HistogramBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#2E2E2E")), 1);
    private static readonly IPen DiagonalPen = new Pen(new SolidColorBrush(Color.Parse("#3C3C3C")), 1, new DashStyle([3, 3], 0));

    private List<CurvePoint> _points = [.. Curve.Identity.Points];
    private int[]? _histogram;
    private int _drag = -1;
    private bool _dragOutside;
    private Color _color = Colors.White;

    /// <summary>Крива змінилася (під час перетягування — на кожен рух).</summary>
    public event Action<Curve>? CurveChanged;

    public Color CurveColor
    {
        get => _color;
        set { _color = value; InvalidateVisual(); }
    }

    public void SetCurve(Curve curve)
    {
        _points = [.. curve.Points];
        _drag = -1;
        InvalidateVisual();
    }

    /// <summary>256 стовпчиків гістограми для фону (або null).</summary>
    public void SetHistogram(int[]? bins)
    {
        _histogram = bins;
        InvalidateVisual();
    }

    private Rect Area => new(Pad, Pad, Math.Max(1, Bounds.Width - 2 * Pad), Math.Max(1, Bounds.Height - 2 * Pad));

    private Point ToScreen(CurvePoint p)
    {
        var a = Area;
        return new Point(a.X + p.X * a.Width, a.Bottom - p.Y * a.Height);
    }

    private CurvePoint FromScreen(Point s)
    {
        var a = Area;
        return new CurvePoint(Math.Clamp((s.X - a.X) / a.Width, 0, 1), Math.Clamp((a.Bottom - s.Y) / a.Height, 0, 1));
    }

    /// <summary>Крива без точки, яку зараз витягують за межі (вона видалиться, якщо її так і відпустити).</summary>
    private Curve CurrentCurve() =>
        new(_dragOutside ? _points.Where((_, i) => i != _drag).ToList() : _points);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        int hit = HitTest(pos);

        if (e.ClickCount == 2)
        {
            if (hit > 0 && hit < _points.Count - 1)
            {
                _points.RemoveAt(hit);
                Changed();
            }
            e.Handled = true;
            return;
        }

        if (hit < 0)
        {
            // Нова точка там, де клікнули: між сусідніми за X.
            var p = FromScreen(pos);
            int index = _points.FindIndex(q => q.X > p.X);
            if (index <= 0) return; // лівіше чорної чи правіше білої точки нових не додаємо
            if (p.X - _points[index - 1].X < MinGap || _points[index].X - p.X < MinGap) return;
            _points.Insert(index, p);
            hit = index;
            Changed();
        }

        _drag = hit;
        _dragOutside = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag < 0) return;
        var pos = e.GetPosition(this);
        var p = FromScreen(pos);
        bool endpoint = _drag == 0 || _drag == _points.Count - 1;

        // Точка не може перескочити сусідню: X лишається між ними.
        double minX = _drag == 0 ? 0 : _points[_drag - 1].X + MinGap;
        double maxX = _drag == _points.Count - 1 ? 1 : _points[_drag + 1].X - MinGap;
        _points[_drag] = new CurvePoint(Math.Clamp(p.X, minX, maxX), p.Y);

        var a = Area;
        _dragOutside = !endpoint && (pos.Y < a.Top - RemoveDistance || pos.Y > a.Bottom + RemoveDistance
                                     || pos.X < a.Left - RemoveDistance || pos.X > a.Right + RemoveDistance);
        Changed();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        if (_dragOutside) _points.RemoveAt(_drag);
        _drag = -1;
        _dragOutside = false;
        e.Pointer.Capture(null);
        Changed();
    }

    private int HitTest(Point pos)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            var s = ToScreen(_points[i]);
            if (Math.Abs(s.X - pos.X) <= HitRadius && Math.Abs(s.Y - pos.Y) <= HitRadius) return i;
        }
        return -1;
    }

    private void Changed()
    {
        InvalidateVisual();
        CurveChanged?.Invoke(CurrentCurve());
    }

    public override void Render(DrawingContext context)
    {
        var a = Area;
        context.FillRectangle(Background, new Rect(Bounds.Size), 4);

        if (_histogram is { Length: > 2 } bins)
        {
            int peak = 1;
            for (int i = 1; i < bins.Length - 1; i++) peak = Math.Max(peak, bins[i]);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(a.Left, a.Bottom), true);
                for (int i = 0; i < bins.Length; i++)
                {
                    double v = Math.Sqrt(Math.Min(1, bins[i] / (double)peak));
                    g.LineTo(new Point(a.Left + i * a.Width / (bins.Length - 1), a.Bottom - v * a.Height * 0.9));
                }
                g.LineTo(new Point(a.Right, a.Bottom));
                g.EndFigure(true);
            }
            context.DrawGeometry(HistogramBrush, null, geometry);
        }

        for (int i = 1; i < 4; i++)
        {
            double x = a.Left + a.Width * i / 4, y = a.Top + a.Height * i / 4;
            context.DrawLine(GridPen, new Point(x, a.Top), new Point(x, a.Bottom));
            context.DrawLine(GridPen, new Point(a.Left, y), new Point(a.Right, y));
        }
        context.DrawLine(DiagonalPen, new Point(a.Left, a.Bottom), new Point(a.Right, a.Top));

        var curve = CurrentCurve();
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            const int steps = 160;
            for (int i = 0; i <= steps; i++)
            {
                double x = i / (double)steps;
                var pt = ToScreen(new CurvePoint(x, curve.Evaluate(x)));
                if (i == 0) g.BeginFigure(pt, false); else g.LineTo(pt);
            }
            g.EndFigure(false);
        }
        var brush = new SolidColorBrush(IsEnabled ? _color : Color.FromArgb(90, _color.R, _color.G, _color.B));
        context.DrawGeometry(null, new Pen(brush, 1.6), line);

        for (int i = 0; i < _points.Count; i++)
        {
            if (_dragOutside && i == _drag) continue;
            var s = ToScreen(_points[i]);
            context.DrawEllipse(i == _drag ? brush : Background, new Pen(brush, 1.5), s, 4.5, 4.5);
        }
    }
}
