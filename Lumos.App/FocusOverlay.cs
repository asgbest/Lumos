using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>Вибір точки фокуса для розмиття об'єктива: приціл під курсором і поточний фокус; клік — новий фокус.</summary>
public sealed class FocusOverlay : Control
{
    private static readonly IPen SolidPen = new Pen(Brushes.White, 1.5);
    private static readonly IPen ShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3);
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#8F82EE"));

    private double _cropX, _cropY, _cropW = 1, _cropH = 1, _focusX = -1, _focusY = -1;
    private int _outW = 1, _outH = 1;
    private Point? _pointer;

    /// <summary>Вибрано фокус (частки полотна до обрізки).</summary>
    public event Action<double, double>? FocusPicked;

    public FocusOverlay() => Cursor = new Cursor(StandardCursorType.Cross);

    public void Update(DevelopSettings s, int outputWidth, int outputHeight)
    {
        (_cropX, _cropY, _cropW, _cropH) = (s.CropX, s.CropY, Math.Max(1e-6, s.CropW), Math.Max(1e-6, s.CropH));
        (_outW, _outH) = (Math.Max(1, outputWidth), Math.Max(1, outputHeight));
        (_focusX, _focusY) = (s.BlurFocusX, s.BlurFocusY);
        InvalidateVisual();
    }

    private Rect ImageRect()
    {
        double scale = Math.Min(Bounds.Width / _outW, Bounds.Height / _outH);
        double w = _outW * scale, h = _outH * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var r = ImageRect();
        var p = e.GetPosition(this);
        if (!r.Contains(p)) return;
        e.Handled = true;
        FocusPicked?.Invoke(_cropX + (p.X - r.X) / r.Width * _cropW, _cropY + (p.Y - r.Y) / r.Height * _cropH);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
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
        if (_focusX >= 0)
        {
            var f = new Point(r.X + (_focusX - _cropX) / _cropW * r.Width, r.Y + (_focusY - _cropY) / _cropH * r.Height);
            context.DrawEllipse(null, ShadowPen, f, 14, 14);
            context.DrawEllipse(null, new Pen(Accent, 2), f, 14, 14);
            context.DrawEllipse(Accent, null, f, 3, 3);
        }
        if (_pointer is { } p)
        {
            foreach (var (a, b) in new[] { (new Point(p.X - 18, p.Y), new Point(p.X - 6, p.Y)), (new Point(p.X + 6, p.Y), new Point(p.X + 18, p.Y)),
                                          (new Point(p.X, p.Y - 18), new Point(p.X, p.Y - 6)), (new Point(p.X, p.Y + 6), new Point(p.X, p.Y + 18)) })
            {
                context.DrawLine(ShadowPen, a, b);
                context.DrawLine(SolidPen, a, b);
            }
        }
    }
}
