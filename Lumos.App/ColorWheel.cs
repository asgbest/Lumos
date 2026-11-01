using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>
/// Колесо кольорового грейдування: кут — відтінок (0° = червоний угорі, далі за годинниковою стрілкою), відстань від центру — сила.
/// Тягнути — вибрати колір; подвійний клік — скинути в центр (без відтінку).
/// </summary>
public sealed class ColorWheel : Control
{
    private static readonly IBrush Rainbow = new ConicGradientBrush
    {
        Center = RelativePoint.Center,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(255, 0, 0), 0),
            new GradientStop(Color.FromRgb(255, 255, 0), 1 / 6.0),
            new GradientStop(Color.FromRgb(0, 255, 0), 2 / 6.0),
            new GradientStop(Color.FromRgb(0, 255, 255), 3 / 6.0),
            new GradientStop(Color.FromRgb(0, 0, 255), 4 / 6.0),
            new GradientStop(Color.FromRgb(255, 0, 255), 5 / 6.0),
            new GradientStop(Color.FromRgb(255, 0, 0), 1),
        },
    };

    // Ближче до центру колір блякне до сірого — так видно, що відстань означає силу.
    private static readonly IBrush Fade = new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromRgb(70, 70, 70), 0),
            new GradientStop(Color.FromArgb(0, 70, 70, 70), 1),
        },
    };

    private static readonly IPen RimPen = new Pen(new SolidColorBrush(Color.Parse("#1A1A1A")), 2);
    private static readonly IPen CrossPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    private static readonly IPen PuckPen = new Pen(Brushes.White, 2);

    private bool _dragging;

    public double Hue { get; private set; }
    public double Saturation { get; private set; }

    /// <summary>Користувач змінив колір: (відтінок 0…360, сила 0…100).</summary>
    public event Action<double, double>? ValueChanged;

    public void SetValue(double hue, double saturation)
    {
        Hue = hue;
        Saturation = saturation;
        InvalidateVisual();
    }

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double Radius => Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 3);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            Set(0, 0);
            e.Handled = true;
            return;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Pick(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Pick(Point pos)
    {
        double dx = pos.X - Center.X, dy = pos.Y - Center.Y;
        double r = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / Radius);
        double hue = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        Set(Math.Round(hue), Math.Round(r * 100));
    }

    private void Set(double hue, double saturation)
    {
        if (hue == Hue && saturation == Saturation) return;
        SetValue(hue, saturation);
        ValueChanged?.Invoke(hue, saturation);
    }

    public override void Render(DrawingContext context)
    {
        var c = Center;
        double r = Radius;
        using (context.PushOpacity(IsEnabled ? 1 : 0.45))
        {
            context.DrawEllipse(Rainbow, RimPen, c, r, r);
            context.DrawEllipse(Fade, null, c, r, r);
            context.DrawLine(CrossPen, new Point(c.X - 5, c.Y), new Point(c.X + 5, c.Y));
            context.DrawLine(CrossPen, new Point(c.X, c.Y - 5), new Point(c.X, c.Y + 5));

            double a = Hue * Math.PI / 180, d = Saturation / 100 * r;
            var puck = new Point(c.X + Math.Sin(a) * d, c.Y - Math.Cos(a) * d);
            context.DrawEllipse(null, new Pen(Brushes.Black, 4), puck, 6, 6);
            context.DrawEllipse(null, PuckPen, puck, 6, 6);
        }
    }
}
