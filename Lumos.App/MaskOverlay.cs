using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Редагування вибраної маски прямо на фото:
/// лінійна — три лінії (повна сила / середина / нуль), тягнуться кінці або середина;
/// радіальна — еліпс із ручками розміру й центру та ручкою повороту над ним (Shift — кратно 15°),
/// пунктир — де починається розмиття;
/// пензель — малювання мишкою (Alt або «Гумка» — стирати), коло показує розмір і м'якість.
/// Координати маски — частки полотна до обрізки; на екрані показується вже обрізаний кадр.
/// </summary>
public sealed class MaskOverlay : Control
{
    private const double HandleHit = 14;
    private const double RotateOffset = 28; // наскільки ручка повороту винесена за еліпс

    private static readonly IPen SolidPen = new Pen(Brushes.White, 1.5);
    private static readonly IPen ShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3);
    private static readonly IPen DashPen = new Pen(Brushes.White, 1, new DashStyle([4, 4], 0));
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.Parse("#4DA3FF"));

    private Mask? _mask;
    private double _cropX, _cropY, _cropW = 1, _cropH = 1;
    private int _outW = 1, _outH = 1;
    private double _brushSize = 0.06, _brushFeather = 0.5, _brushFlow = 1;
    private bool _brushErase;

    private enum Drag { None, Point1, Point2, Move, RadiusX, RadiusY, Rotate, Paint }
    private Drag _drag;
    private Point _dragStart;
    private Mask? _dragMask;
    private List<MaskPoint>? _stroke;
    private bool _strokeErase;
    private Point? _pointer;

    private bool _picking;

    /// <summary>Маску змінили (на кожен рух).</summary>
    public event Action<Mask>? MaskChanged;

    /// <summary>Клік піпеткою: точка полотна і чи затиснуто Shift (додати колір, а не замінити).</summary>
    public event Action<MaskPoint, bool>? Picked;

    /// <param name="picking">Піпетка (маска за кольором/яскравістю): клік бере колір, а не малює й не тягне ручки.</param>
    public void Update(Mask? mask, DevelopSettings s, int outputWidth, int outputHeight,
        double brushSize, double brushFeather, double brushFlow, bool brushErase, bool picking)
    {
        if (_drag == Drag.None) _mask = mask;
        (_cropX, _cropY, _cropW, _cropH) = (s.CropX, s.CropY, Math.Max(1e-6, s.CropW), Math.Max(1e-6, s.CropH));
        (_outW, _outH) = (Math.Max(1, outputWidth), Math.Max(1, outputHeight));
        (_brushSize, _brushFeather, _brushFlow, _brushErase) = (brushSize, brushFeather, brushFlow, brushErase);
        _picking = picking;
        Cursor = new Cursor(picking || Paints(mask) ? StandardCursorType.Cross : StandardCursorType.Arrow);
        InvalidateVisual();
    }

    /// <summary>Маски, які малюють і доправляють пензлем.</summary>
    private static bool Paints(Mask? mask) => mask is { Kind: MaskKind.Brush } or { IsAi: true } or { IsRange: true };

    /// <summary>Де на екрані лежить кадр (фото вписане по центру, як Image зі Stretch=Uniform).</summary>
    private Rect ImageRect()
    {
        double scale = Math.Min(Bounds.Width / _outW, Bounds.Height / _outH);
        double w = _outW * scale, h = _outH * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    // Полотно (до обрізки) на екрані: кадр — його частина CropW × CropH.
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

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= HandleHit && Math.Abs(a.Y - b.Y) <= HandleHit;

    /// <summary>Точка в осях повернутого еліпса (x — уздовж півосі X, y — уздовж Y) → точка на екрані.</summary>
    private Point OnEllipse(Mask m, double x, double y)
    {
        var c = ToScreen(m.X1, m.Y1);
        double a = m.Angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        return new Point(c.X + x * cos - y * sin, c.Y + x * sin + y * cos);
    }

    /// <summary>Відстань курсора від центру еліпса вздовж його осі (0 — X, 1 — Y) у пікселях екрана.</summary>
    private double AlongAxis(Point p, Mask m, int axis)
    {
        var c = ToScreen(m.X1, m.Y1);
        double a = m.Angle * Math.PI / 180, dx = p.X - c.X, dy = p.Y - c.Y;
        return axis == 0 ? dx * Math.Cos(a) + dy * Math.Sin(a) : -dx * Math.Sin(a) + dy * Math.Cos(a);
    }

    /// <summary>Кут, на який показує курсор від центру (0° — угору, за годинниковою стрілкою), з кроком 1° або 15° (Shift).</summary>
    private double AngleTo(Point p, Mask m, bool snap)
    {
        var c = ToScreen(m.X1, m.Y1);
        double degrees = Math.Atan2(p.X - c.X, -(p.Y - c.Y)) * 180 / Math.PI;
        double step = snap ? 15 : 1;
        degrees = Math.Round(degrees / step) * step;
        return degrees > 180 ? degrees - 360 : degrees <= -180 ? degrees + 360 : degrees;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_mask is not { } mask || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _drag = Drag.None;
        if (_picking)
        {
            if (ImageRect().Contains(p)) Picked?.Invoke(ToCanvas(p), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
            return;
        }
        switch (mask.Kind)
        {
            case MaskKind.Linear:
            {
                Point p1 = ToScreen(mask.X1, mask.Y1), p2 = ToScreen(mask.X2, mask.Y2);
                if (Near(p, p1)) _drag = Drag.Point1;
                else if (Near(p, p2)) _drag = Drag.Point2;
                else if (Near(p, (p1 + p2) / 2)) _drag = Drag.Move;
                break;
            }
            case MaskKind.Radial:
            {
                double rx = mask.RadiusX * CanvasW, ry = mask.RadiusY * CanvasH;
                if (Near(p, OnEllipse(mask, 0, -(ry + RotateOffset)))) _drag = Drag.Rotate;
                else if (Near(p, ToScreen(mask.X1, mask.Y1))) _drag = Drag.Move;
                else if (Near(p, OnEllipse(mask, rx, 0)) || Near(p, OnEllipse(mask, -rx, 0))) _drag = Drag.RadiusX;
                else if (Near(p, OnEllipse(mask, 0, ry)) || Near(p, OnEllipse(mask, 0, -ry))) _drag = Drag.RadiusY;
                break;
            }
            default:
                _drag = Drag.Paint;
                _stroke = [ToCanvas(p)];
                _strokeErase = _brushErase || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                break;
        }
        if (_drag == Drag.None) return;
        _dragStart = p;
        _dragMask = mask;
        e.Pointer.Capture(this);
        e.Handled = true;
        if (_drag == Drag.Paint) Emit(WithStroke());
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        if (_drag == Drag.None || _dragMask is not { } start)
        {
            if (Paints(_mask) || _picking) InvalidateVisual();
            return;
        }

        var c = ToCanvas(p);
        double du = (p.X - _dragStart.X) / CanvasW, dv = (p.Y - _dragStart.Y) / CanvasH;
        Mask? next = _drag switch
        {
            Drag.Point1 => start with { X1 = c.X, Y1 = c.Y },
            Drag.Point2 => start with { X2 = c.X, Y2 = c.Y },
            Drag.Move when start.Kind == MaskKind.Linear => start with { X1 = start.X1 + du, Y1 = start.Y1 + dv, X2 = start.X2 + du, Y2 = start.Y2 + dv },
            Drag.Move => start with { X1 = start.X1 + du, Y1 = start.Y1 + dv },
            // Розмір — уздовж осей повернутого еліпса, тож тягнути можна під будь-яким кутом.
            Drag.RadiusX => start with { RadiusX = Math.Max(0.01, Math.Abs(AlongAxis(p, start, 0)) / CanvasW) },
            Drag.RadiusY => start with { RadiusY = Math.Max(0.01, Math.Abs(AlongAxis(p, start, 1)) / CanvasH) },
            Drag.Rotate => start with { Angle = AngleTo(p, start, e.KeyModifiers.HasFlag(KeyModifiers.Shift)) },
            Drag.Paint => AddPoint(c),
            _ => null,
        };
        if (next is not null) Emit(next);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        _dragMask = null;
        _stroke = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    private Mask? AddPoint(MaskPoint c)
    {
        if (_stroke is null) return null;
        var last = ToScreen(_stroke[^1].X, _stroke[^1].Y);
        var now = ToScreen(c.X, c.Y);
        if (Math.Abs(now.X - last.X) + Math.Abs(now.Y - last.Y) < 3) return null; // не плодимо точки на місці
        _stroke.Add(c);
        return WithStroke();
    }

    /// <summary>Маска на момент натискання + мазок, що зараз малюється.</summary>
    private Mask WithStroke() =>
        _dragMask! with
        {
            Strokes = _dragMask!.Strokes.Add(new BrushStroke(_brushSize, _brushFeather, _brushFlow, _strokeErase, new(_stroke!))),
        };

    private void Emit(Mask mask)
    {
        _mask = mask;
        InvalidateVisual();
        MaskChanged?.Invoke(mask);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // щоб миша «бачила» всю область
        if (_mask is not { } mask) return;
        var r = ImageRect();
        using var clip = context.PushClip(r);

        if (_picking)
        {
            DrawPicker(context);
            return;
        }
        switch (mask.Kind)
        {
            case MaskKind.Linear: DrawLinear(context, mask, r); break;
            case MaskKind.Radial: DrawRadial(context, mask); break;
            default: DrawBrushCursor(context); break;
        }
    }

    private void DrawLinear(DrawingContext context, Mask mask, Rect r)
    {
        Point p1 = ToScreen(mask.X1, mask.Y1), p2 = ToScreen(mask.X2, mask.Y2), mid = (p1 + p2) / 2;
        var d = p2 - p1;
        double len = Math.Max(1e-6, Math.Sqrt(d.X * d.X + d.Y * d.Y));
        var n = new Vector(-d.Y / len, d.X / len) * (r.Width + r.Height); // перпендикуляр через увесь кадр
        foreach (var (point, pen) in new[] { (p1, SolidPen), (mid, DashPen), (p2, SolidPen) })
        {
            context.DrawLine(ShadowPen, point - n, point + n);
            context.DrawLine(pen, point - n, point + n);
        }
        context.DrawLine(DashPen, p1, p2);
        Handle(context, p1);
        Handle(context, p2);
        context.DrawRectangle(HandleFill, SolidPen, new Rect(mid.X - 5, mid.Y - 5, 10, 10));
    }

    private void DrawRadial(DrawingContext context, Mask mask)
    {
        var c = ToScreen(mask.X1, mask.Y1);
        double rx = mask.RadiusX * CanvasW, ry = mask.RadiusY * CanvasH;
        double inner = 1 - Math.Clamp(mask.Feather / 100, 0.01, 1);
        using (context.PushTransform(Matrix.CreateRotation(mask.Angle * Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y)))
        {
            context.DrawEllipse(null, ShadowPen, new Point(), rx, ry);
            context.DrawEllipse(null, SolidPen, new Point(), rx, ry);
            context.DrawEllipse(null, DashPen, new Point(), rx * inner, ry * inner);
        }
        context.DrawRectangle(HandleFill, SolidPen, new Rect(c.X - 5, c.Y - 5, 10, 10));
        foreach (var h in new[] { OnEllipse(mask, rx, 0), OnEllipse(mask, -rx, 0), OnEllipse(mask, 0, ry), OnEllipse(mask, 0, -ry) })
            Handle(context, h);

        // Ручка повороту — на «ніжці» над верхньою ручкою, порожнє коло, щоб відрізнялась від ручок розміру.
        Point top = OnEllipse(mask, 0, -ry), rotate = OnEllipse(mask, 0, -(ry + RotateOffset));
        context.DrawLine(ShadowPen, top, rotate);
        context.DrawLine(SolidPen, top, rotate);
        context.DrawEllipse(Brushes.Black, ShadowPen, rotate, 7, 7);
        context.DrawEllipse(null, new Pen(HandleFill, 2.5), rotate, 6, 6);
    }

    private void DrawBrushCursor(DrawingContext context)
    {
        if (_pointer is not { } p) return;
        double radius = _brushSize * Math.Max(CanvasW, CanvasH) / 2;
        context.DrawEllipse(null, ShadowPen, p, radius, radius);
        context.DrawEllipse(null, SolidPen, p, radius, radius);
        double hard = radius * (1 - _brushFeather);
        if (hard > 2) context.DrawEllipse(null, DashPen, p, hard, hard);
        if (_brushErase) context.DrawLine(SolidPen, new Point(p.X - 5, p.Y), new Point(p.X + 5, p.Y)); // «мінус» — гумка
    }

    /// <summary>Піпетка: маленьке коло (те, що усереднюється) з перехрестям.</summary>
    private void DrawPicker(DrawingContext context)
    {
        if (_pointer is not { } p) return;
        context.DrawEllipse(null, ShadowPen, p, 6, 6);
        context.DrawEllipse(null, SolidPen, p, 6, 6);
        foreach (var (a, b) in new[] { (new Point(p.X - 14, p.Y), new Point(p.X - 8, p.Y)), (new Point(p.X + 8, p.Y), new Point(p.X + 14, p.Y)),
                     (new Point(p.X, p.Y - 14), new Point(p.X, p.Y - 8)), (new Point(p.X, p.Y + 8), new Point(p.X, p.Y + 14)) })
        {
            context.DrawLine(ShadowPen, a, b);
            context.DrawLine(SolidPen, a, b);
        }
    }

    private static void Handle(DrawingContext context, Point p)
    {
        context.DrawEllipse(HandleFill, ShadowPen, p, 6, 6);
        context.DrawEllipse(HandleFill, SolidPen, p, 6, 6);
    }
}
