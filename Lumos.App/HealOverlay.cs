using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Відновлювальний пензель на фото: мазок (чи клік) — нова латка; у вибраної латки видно, звідки вона береться
/// (пунктир) — це джерело можна перетягнути. Інші латки позначені кружечками: клік — вибрати.
/// Координати — частки полотна до обрізки, як у масок.
/// </summary>
public sealed class HealOverlay : Control
{
    private static readonly IPen SolidPen = new Pen(Brushes.White, 1.5);
    private static readonly IPen ShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3);
    private static readonly IPen DashPen = new Pen(Brushes.White, 1.5, new DashStyle([4, 3], 0));
    private static readonly IBrush PinFill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.Parse("#8F82EE"));

    private EquatableList<HealSpot> _heals = EquatableList<HealSpot>.Empty;
    private int _selected = -1;
    private double _cropX, _cropY, _cropW = 1, _cropH = 1;
    private int _outW = 1, _outH = 1;
    private double _size = 0.015, _feather = 0.5;

    private enum Drag { None, Paint, Source }
    private Drag _drag;
    private List<MaskPoint>? _stroke;
    private Point _dragStart;
    private HealSpot? _dragSpot;
    private Point? _pointer;

    /// <summary>Мазок нової латки завершено.</summary>
    public event Action<IReadOnlyList<MaskPoint>>? StrokeFinished;

    /// <summary>Клік по латці — вибрати її (−1 — зняти вибір).</summary>
    public event Action<int>? SpotSelected;

    /// <summary>Джерело вибраної латки перетягують: новий зсув (на кожен рух).</summary>
    public event Action<double, double>? SourceMoved;

    public HealOverlay() => Cursor = new Cursor(StandardCursorType.Cross);

    public void Update(EquatableList<HealSpot> heals, int selected, DevelopSettings s, int outputWidth, int outputHeight, double size, double feather)
    {
        if (_drag != Drag.Source) _heals = heals;
        _selected = selected;
        (_cropX, _cropY, _cropW, _cropH) = (s.CropX, s.CropY, Math.Max(1e-6, s.CropW), Math.Max(1e-6, s.CropH));
        (_outW, _outH) = (Math.Max(1, outputWidth), Math.Max(1, outputHeight));
        (_size, _feather) = (size, feather);
        InvalidateVisual();
    }

    private Rect ImageRect()
    {
        double scale = Math.Min(Bounds.Width / _outW, Bounds.Height / _outH);
        double w = _outW * scale, h = _outH * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private double CanvasW => ImageRect().Width / _cropW;
    private double CanvasH => ImageRect().Height / _cropH;

    private Point ToScreen(double u, double v)
    {
        var r = ImageRect();
        return new Point(r.X + (u - _cropX) / _cropW * r.Width, r.Y + (v - _cropY) / _cropH * r.Height);
    }

    private MaskPoint ToCanvas(Point p)
    {
        var r = ImageRect();
        return new MaskPoint(_cropX + (p.X - r.X) / r.Width * _cropW, _cropY + (p.Y - r.Y) / r.Height * _cropH);
    }

    private double RadiusOnScreen(double size) => size * Math.Max(CanvasW, CanvasH) / 2;

    /// <summary>Точки латки на екрані (за потреби — зсунуті на джерело).</summary>
    private List<Point> PathOnScreen(HealSpot spot, bool source) =>
        spot.Points.Select(p => ToScreen(p.X + (source ? spot.SourceDX : 0), p.Y + (source ? spot.SourceDY : 0))).ToList();

    private static double DistanceToPath(Point p, List<Point> path)
    {
        if (path.Count == 1) return Distance(p, path[0]);
        double best = double.MaxValue;
        for (int i = 1; i < path.Count; i++)
        {
            Point a = path[i - 1], b = path[i];
            var ab = b - a;
            double len2 = ab.X * ab.X + ab.Y * ab.Y;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
            best = Math.Min(best, Distance(p, a + ab * t));
        }
        return best;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;

        // 1) Джерело вибраної латки — тягнути.
        if (_selected >= 0 && _selected < _heals.Count && _heals[_selected].Mode != HealMode.Remove) // у ШІ-видалення джерела немає
        {
            var spot = _heals[_selected];
            if (DistanceToPath(p, PathOnScreen(spot, source: true)) <= Math.Max(8, RadiusOnScreen(spot.Size)))
            {
                (_drag, _dragStart, _dragSpot) = (Drag.Source, p, spot);
                return;
            }
        }
        // 2) Інша латка — вибрати (остання зверху).
        for (int i = _heals.Count - 1; i >= 0; i--)
            if (DistanceToPath(p, PathOnScreen(_heals[i], source: false)) <= Math.Max(8, RadiusOnScreen(_heals[i].Size)))
            {
                _drag = Drag.None;
                SpotSelected?.Invoke(i == _selected ? -1 : i);
                return;
            }
        // 3) Нова латка.
        _drag = Drag.Paint;
        _stroke = [ToCanvas(p)];
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        switch (_drag)
        {
            case Drag.Paint when _stroke is not null:
                var last = ToScreen(_stroke[^1].X, _stroke[^1].Y);
                if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 3) _stroke.Add(ToCanvas(p));
                break;
            case Drag.Source when _dragSpot is { } start:
                double dx = start.SourceDX + (p.X - _dragStart.X) / CanvasW, dy = start.SourceDY + (p.Y - _dragStart.Y) / CanvasH;
                if (_selected >= 0 && _selected < _heals.Count) _heals = _heals.SetItem(_selected, start with { SourceDX = dx, SourceDY = dy });
                SourceMoved?.Invoke(dx, dy);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        var stroke = _stroke;
        (_drag, _stroke, _dragSpot) = (Drag.None, null, null);
        e.Pointer.Capture(null);
        if (drag == Drag.Paint && stroke is { Count: > 0 }) StrokeFinished?.Invoke(stroke);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // щоб миша «бачила» всю область
        var r = ImageRect();
        using var clip = context.PushClip(r);

        for (int i = 0; i < _heals.Count; i++)
        {
            if (i == _selected) continue;
            var pin = PathOnScreen(_heals[i], source: false)[0];
            context.DrawEllipse(PinFill, ShadowPen, pin, 5, 5);
        }

        if (_selected >= 0 && _selected < _heals.Count)
        {
            var spot = _heals[_selected];
            double radius = RadiusOnScreen(spot.Size);
            List<Point> dest = PathOnScreen(spot, source: false), source = PathOnScreen(spot, source: true);
            if (spot.Mode != HealMode.Remove)
            {
                DrawShape(context, source, radius, DashPen);
                context.DrawLine(ShadowPen, source[0], dest[0]);
                context.DrawLine(DashPen, source[0], dest[0]);
            }
            DrawShape(context, dest, radius, SolidPen);
            context.DrawEllipse(SelectedFill, SolidPen, dest[0], 5, 5);
        }

        if (_drag == Drag.Paint && _stroke is { Count: > 0 })
            DrawShape(context, _stroke.Select(p => ToScreen(p.X, p.Y)).ToList(), RadiusOnScreen(_size), SolidPen, band: true);

        // Курсор: коло пензля (повністю латка) і пунктир — до куди сягає м'який край.
        if (_pointer is { } at && _drag != Drag.Source)
        {
            double radius = RadiusOnScreen(_size);
            context.DrawEllipse(null, ShadowPen, at, radius, radius);
            context.DrawEllipse(null, SolidPen, at, radius, radius);
            double outer = radius * (1 + 0.5 * _feather);
            if (outer - radius > 2) context.DrawEllipse(null, DashPen, at, outer, outer);
        }
    }

    /// <summary>Обрис латки: кругла — коло; мазок — смуга завширшки з пензель.</summary>
    private static void DrawShape(DrawingContext context, List<Point> path, double radius, IPen pen, bool band = false)
    {
        if (path.Count == 1)
        {
            context.DrawEllipse(null, ShadowPen, path[0], radius, radius);
            context.DrawEllipse(null, pen, path[0], radius, radius);
            return;
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(path[0], false);
            foreach (var p in path.Skip(1)) g.LineTo(p);
            g.EndFigure(false);
        }
        var bandPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(band ? 90 : 60), 255, 255, 255)), radius * 2,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, bandPen, geometry);
        context.DrawGeometry(null, ShadowPen, geometry);
        context.DrawGeometry(null, pen, geometry);
    }
}
