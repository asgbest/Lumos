using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Рамка обрізки поверх фото (режим «✂️ Обрізка»): кути — змінити розмір, сторони — посунути одну межу (у вільних пропорціях),
/// всередині — пересунути рамку. Поза рамкою фото притемнене, всередині — сітка третин.
/// Рамка не виходить ні за полотно, ні за край вирівняного й виправленого фото (щоб не було чорних кутів).
/// Значення — у частках 0…1 від полотна, як у <see cref="DevelopSettings"/>.
/// </summary>
public sealed class CropOverlay : Control
{
    private const double CornerHit = 16, EdgeHit = 10, MinSizePx = 24;

    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1);
    private static readonly IPen ThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    private static readonly IPen HandlePen = new Pen(Brushes.White, 3, lineCap: PenLineCap.Square);

    private enum Mode { None, Move, Corner, Edge }

    private Mode _mode;
    private int _handle;           // кут 0..3 (TL, TR, BR, BL) або сторона 0..3 (верх, право, низ, ліво)
    private Rect _start;           // рамка на початку перетягування (пікселі полотна)
    private Point _startPointer;   // курсор на початку (пікселі полотна)

    public int CanvasWidth { get; private set; } = 1;
    public int CanvasHeight { get; private set; } = 1;
    /// <summary>Вирівнювання, об'єктив і перспектива — щоб рамка не захоплювала чорних країв.</summary>
    public Warp Warp { get; private set; } = new(default(WarpParams) with { Scale = 100, VignetteMid = 50 }, 1, 1);

    /// <summary>Пропорції рамки (ширина/висота в пікселях) або null — вільні.</summary>
    public double? Aspect { get; set; }

    /// <summary>Рамка в частках полотна.</summary>
    public Rect Crop { get; private set; } = new(0, 0, 1, 1);

    /// <summary>Користувач змінив рамку (на кожен рух).</summary>
    public event Action<Rect>? CropChanged;

    public void Update(int canvasWidth, int canvasHeight, WarpParams warp, Rect crop)
    {
        CanvasWidth = Math.Max(1, canvasWidth);
        CanvasHeight = Math.Max(1, canvasHeight);
        Warp = new Warp(warp, CanvasWidth, CanvasHeight);
        Crop = crop;
        InvalidateVisual();
    }

    /// <summary>Де на екрані лежить полотно: фото вписане по центру (як Image зі Stretch=Uniform).</summary>
    private Rect ImageRect()
    {
        double scale = Math.Min(Bounds.Width / CanvasWidth, Bounds.Height / CanvasHeight);
        double w = CanvasWidth * scale, h = CanvasHeight * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private double Scale => ImageRect().Width / CanvasWidth;

    private Rect CropPx => new(Crop.X * CanvasWidth, Crop.Y * CanvasHeight, Crop.Width * CanvasWidth, Crop.Height * CanvasHeight);

    private Rect ToScreen(Rect px)
    {
        var img = ImageRect();
        double s = Scale;
        return new Rect(img.X + px.X * s, img.Y + px.Y * s, px.Width * s, px.Height * s);
    }

    private Point ToCanvas(Point screen)
    {
        var img = ImageRect();
        double s = Scale;
        return new Point((screen.X - img.X) / s, (screen.Y - img.Y) / s);
    }

    private bool Valid(Rect px) =>
        px.Width >= MinSizePx && px.Height >= MinSizePx &&
        ImageGeometry.IsInside(px.X, px.Y, px.Width, px.Height, Warp);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        var r = ToScreen(CropPx);
        Point[] corners = [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];

        _mode = Mode.None;
        for (int i = 0; i < 4; i++)
            if (Math.Abs(pos.X - corners[i].X) <= CornerHit && Math.Abs(pos.Y - corners[i].Y) <= CornerHit)
            {
                _mode = Mode.Corner;
                _handle = i;
                break;
            }

        if (_mode == Mode.None && Aspect is null)
        {
            bool inX = pos.X > r.Left && pos.X < r.Right, inY = pos.Y > r.Top && pos.Y < r.Bottom;
            if (inX && Math.Abs(pos.Y - r.Top) <= EdgeHit) (_mode, _handle) = (Mode.Edge, 0);
            else if (inY && Math.Abs(pos.X - r.Right) <= EdgeHit) (_mode, _handle) = (Mode.Edge, 1);
            else if (inX && Math.Abs(pos.Y - r.Bottom) <= EdgeHit) (_mode, _handle) = (Mode.Edge, 2);
            else if (inY && Math.Abs(pos.X - r.Left) <= EdgeHit) (_mode, _handle) = (Mode.Edge, 3);
        }
        if (_mode == Mode.None && r.Contains(pos)) _mode = Mode.Move;
        if (_mode == Mode.None) return;

        _start = CropPx;
        _startPointer = ToCanvas(pos);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_mode == Mode.None)
        {
            Cursor = new Cursor(HoverCursor(pos));
            return;
        }

        var p = ToCanvas(pos);
        Rect? next = _mode switch
        {
            Mode.Move => Moved(p),
            Mode.Corner => Resized(p),
            _ => EdgeMoved(p),
        };
        if (next is { } px)
        {
            Crop = new Rect(px.X / CanvasWidth, px.Y / CanvasHeight, px.Width / CanvasWidth, px.Height / CanvasHeight);
            InvalidateVisual();
            CropChanged?.Invoke(Crop);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _mode = Mode.None;
        e.Pointer.Capture(null);
    }

    private StandardCursorType HoverCursor(Point pos)
    {
        var r = ToScreen(CropPx);
        Point[] corners = [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];
        for (int i = 0; i < 4; i++)
            if (Math.Abs(pos.X - corners[i].X) <= CornerHit && Math.Abs(pos.Y - corners[i].Y) <= CornerHit)
                return i % 2 == 0 ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner;
        return r.Contains(pos) ? StandardCursorType.SizeAll : StandardCursorType.Arrow;
    }

    /// <summary>Найбільша частка t ∈ [0, 1] руху, за якої рамка ще коректна (рух від коректного стану — «монотонний»).</summary>
    private static double Fit(Func<double, Rect> rectAt, Func<Rect, bool> valid)
    {
        if (valid(rectAt(1))) return 1;
        double lo = 0, hi = 1;
        for (int i = 0; i < 20; i++)
        {
            double mid = (lo + hi) / 2;
            if (valid(rectAt(mid))) lo = mid; else hi = mid;
        }
        return lo;
    }

    private Rect? Moved(Point p)
    {
        double dx = p.X - _startPointer.X, dy = p.Y - _startPointer.Y;
        // Окремо по осях, щоб рамка «ковзала» вздовж краю, а не застрягала.
        Rect r = _start;
        double tx = Fit(t => r.Translate(new Vector(dx * t, 0)), Valid);
        r = r.Translate(new Vector(dx * tx, 0));
        double ty = Fit(t => r.Translate(new Vector(0, dy * t)), Valid);
        return r.Translate(new Vector(0, dy * ty));
    }

    private Rect? Resized(Point p)
    {
        // Протилежний кут лишається на місці.
        Point[] corners = [_start.TopLeft, _start.TopRight, _start.BottomRight, _start.BottomLeft];
        Point fixedCorner = corners[(_handle + 2) % 4];
        double dx = p.X - fixedCorner.X, dy = p.Y - fixedCorner.Y;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (Aspect is { } a)
        {
            if (w / Math.Max(h, 1e-9) > a) w = h * a; else h = w / a;
        }
        double sx = Math.Sign(dx == 0 ? 1 : dx), sy = Math.Sign(dy == 0 ? 1 : dy);

        Rect At(double t)
        {
            double cw = w * t, ch = h * t;
            return new Rect(sx < 0 ? fixedCorner.X - cw : fixedCorner.X, sy < 0 ? fixedCorner.Y - ch : fixedCorner.Y, cw, ch);
        }
        double fit = Fit(At, Valid);
        var result = At(fit);
        return result.Width >= MinSizePx && result.Height >= MinSizePx ? result : null;
    }

    private Rect? EdgeMoved(Point p)
    {
        Rect s = _start;
        Rect At(double t) => _handle switch
        {
            0 => new Rect(s.X, s.Y + (p.Y - s.Top) * t, s.Width, s.Height - (p.Y - s.Top) * t),
            1 => new Rect(s.X, s.Y, s.Width + (p.X - s.Right) * t, s.Height),
            2 => new Rect(s.X, s.Y, s.Width, s.Height + (p.Y - s.Bottom) * t),
            _ => new Rect(s.X + (p.X - s.Left) * t, s.Y, s.Width - (p.X - s.Left) * t, s.Height),
        };
        var result = At(Fit(At, r => r.Width >= MinSizePx && r.Height >= MinSizePx && Valid(r)));
        return Valid(result) ? result : null;
    }

    public override void Render(DrawingContext context)
    {
        var img = ImageRect();
        var r = ToScreen(CropPx);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // щоб миша «бачила» і прозору середину рамки

        // Затемнення навколо рамки.
        context.FillRectangle(Dim, new Rect(img.Left, img.Top, img.Width, Math.Max(0, r.Top - img.Top)));
        context.FillRectangle(Dim, new Rect(img.Left, r.Bottom, img.Width, Math.Max(0, img.Bottom - r.Bottom)));
        context.FillRectangle(Dim, new Rect(img.Left, r.Top, Math.Max(0, r.Left - img.Left), r.Height));
        context.FillRectangle(Dim, new Rect(r.Right, r.Top, Math.Max(0, img.Right - r.Right), r.Height));

        for (int i = 1; i < 3; i++)
        {
            double x = r.Left + r.Width * i / 3, y = r.Top + r.Height * i / 3;
            context.DrawLine(ThirdsPen, new Point(x, r.Top), new Point(x, r.Bottom));
            context.DrawLine(ThirdsPen, new Point(r.Left, y), new Point(r.Right, y));
        }
        context.DrawRectangle(null, BorderPen, r);

        // Кутові «скобки».
        const double len = 18;
        foreach (var (corner, hx, vy) in new[]
                 {
                     (r.TopLeft, 1, 1), (r.TopRight, -1, 1), (r.BottomRight, -1, -1), (r.BottomLeft, 1, -1),
                 })
        {
            context.DrawLine(HandlePen, corner, new Point(corner.X + hx * len, corner.Y));
            context.DrawLine(HandlePen, corner, new Point(corner.X, corner.Y + vy * len));
        }

        if (Aspect is null)
        {
            const double half = 10;
            context.DrawLine(HandlePen, new Point(r.Center.X - half, r.Top), new Point(r.Center.X + half, r.Top));
            context.DrawLine(HandlePen, new Point(r.Center.X - half, r.Bottom), new Point(r.Center.X + half, r.Bottom));
            context.DrawLine(HandlePen, new Point(r.Left, r.Center.Y - half), new Point(r.Left, r.Center.Y + half));
            context.DrawLine(HandlePen, new Point(r.Right, r.Center.Y - half), new Point(r.Right, r.Center.Y + half));
        }
    }
}
