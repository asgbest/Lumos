using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace Lumos.App;

/// <summary>
/// Права половина вікна запуску: нічне небо, паличка, «Люмос!» — світло в кільці діафрагми, іскри й зоряний пил.
/// Малюється наживо на потоці рендеру (<see cref="CompositionCustomVisualHandler"/>): діафрагма «дихає» — звужується й
/// розширюється — навіть поки потік інтерфейсу зайнятий створенням головного вікна. Той самий малюнок — у вікнах «✨ AI»,
/// поки чекаємо на Gemini; анімація йде, лише поки малюнок видно.
/// </summary>
public sealed class SplashArt : Control
{
    private CompositionCustomVisual? _visual;

    /// <summary>false — заокруглені лише праві кути (права половина заставки); true — усі.</summary>
    public bool RoundAllCorners { get; init; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor) return;
        _visual = compositor.CreateCustomVisual(new Handler(RoundAllCorners));
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(IsVisible ? Handler.Start : Handler.Stop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) _visual?.SendHandlerMessage(IsVisible ? Handler.Start : Handler.Stop);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.SendHandlerMessage(Handler.Stop);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual is not null) _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    private sealed class Handler(bool roundAll) : CompositionCustomVisualHandler
    {
        public static readonly object Start = new(), Stop = new();
        private bool _running;
        private TimeSpan? _t0;

        public override void OnMessage(object message)
        {
            _running = message == Start;
            if (_running) RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            if (!_running) return;
            Invalidate();
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature skia) return;
            _t0 ??= CompositionNow;
            double t = (CompositionNow - _t0.Value).TotalSeconds;
            using var lease = skia.Lease();
            var c = lease.SkCanvas;
            var size = EffectiveSize;
            c.Save();
            // Заокруглені праві кути, як у вікна (або всі — окрема картинка).
            var frame = new SKRoundRect();
            var corner = new SKPoint(12, 12);
            var left = roundAll ? corner : SKPoint.Empty;
            frame.SetRectRadii(new SKRect(0, 0, (float)size.X, (float)size.Y), [left, corner, corner, left]);
            c.ClipRoundRect(frame, antialias: true);
            // Малюнок задуманий на 860×1000, заповнює рамку (як UniformToFill), по центру.
            float scale = (float)Math.Max(size.X / Art.W, size.Y / Art.H);
            c.Translate((float)(size.X - Art.W * scale) / 2, (float)(size.Y - Art.H * scale) / 2);
            c.Scale(scale);
            Art.Draw(c, t);
            c.Restore();
        }
    }

    private static class Art
    {
        public const float W = 860, H = 1000;
        private static readonly SKPoint Tip = new(500, 400);
        private static readonly SKPoint WandStart = new(60, 1040);

        // Зорі й пил — раз, з тим самим зерном, що й колись у tools\splash.
        private static readonly (SKPoint P, float R, byte A)[] Stars, Dust;

        static Art()
        {
            var rnd = new Random(7);
            Stars = Enumerable.Range(0, 260).Select(_ =>
            {
                float x = rnd.NextSingle() * W, y = rnd.NextSingle() * H, r = 0.6f + rnd.NextSingle() * 1.6f;
                return (new SKPoint(x, y), r, (byte)(60 + rnd.Next(150)));
            }).ToArray();
            Dust = Enumerable.Range(0, 140).Select(_ =>
            {
                float t = 0.35f + rnd.NextSingle() * 0.65f, side = (rnd.NextSingle() - 0.5f) * 220 * (1.1f - t);
                var pt = At(t, side);
                byte a = (byte)(40 + rnd.Next(170) * t);
                return (pt, 0.8f + rnd.NextSingle() * 2.2f, a);
            }).ToArray();
        }

        private static SKPoint At(float t, float side)
        {
            float dx = Tip.X - WandStart.X, dy = Tip.Y - WandStart.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            float nx = -dy / len, ny = dx / len;
            return new(WandStart.X + dx * t + nx * side, WandStart.Y + dy * t + ny * side);
        }

        /// <param name="time">Секунди від появи заставки.</param>
        public static void Draw(SKCanvas c, double time)
        {
            // Діафрагма: плавний цикл «відкрита ↔ прикрита» за 2 с (заставку видно ~1,5 с), починає з того вигляду, що був на картинці.
            const double period = 2.0;
            double phase = Math.Sin(time / period * 2 * Math.PI);
            float open = (float)(0.5 + 0.5 * phase); // 0 — прикрита, 1 — відкрита
            float inner = 108 + (float)phase * (phase > 0 ? 80 : 46); // 62…188
            float spin = -(float)phase * 0.32f; // пелюстки повертаються, як у справжньої діафрагми

            using (var p = new SKPaint { IsAntialias = true })
            {
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
                    [new SKColor(0x22, 0x1C, 0x52), new SKColor(0x0D, 0x10, 0x2A), new SKColor(0x04, 0x05, 0x10)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
                c.DrawRect(0, 0, W, H, p);
            }

            using (var p = new SKPaint { IsAntialias = true })
                foreach (var (pt, r, a) in Stars)
                {
                    p.Color = new SKColor(220, 228, 255, a);
                    c.DrawCircle(pt, r, p);
                }

            // Велике м'яке сяйво — трохи яскравіше, коли діафрагма відкрита.
            using (var p = new SKPaint { IsAntialias = true })
            {
                byte g = (byte)(170 + 30 * open);
                p.Shader = SKShader.CreateRadialGradient(Tip, 640,
                    [new SKColor(255, 250, 230, g), new SKColor(170, 215, 255, (byte)(90 + 30 * open)), new SKColor(90, 110, 255, 40), SKColors.Transparent],
                    [0, 0.16f, 0.5f, 1], SKShaderTileMode.Clamp);
                c.DrawCircle(Tip, 640, p);
            }

            DrawAperture(c, inner, spin);

            using (var p = new SKPaint { IsAntialias = true })
                foreach (var (angle, len, width) in new[] { (0f, 520f, 16f), (90f, 520f, 16f), (45f, 260f, 8f), (135f, 260f, 8f) })
                {
                    c.Save();
                    c.Translate(Tip.X, Tip.Y);
                    c.RotateDegrees(angle);
                    p.Shader = SKShader.CreateLinearGradient(new SKPoint(-len, 0), new SKPoint(len, 0),
                        [SKColors.Transparent, new SKColor(255, 255, 255, 230), SKColors.Transparent], [0, 0.5f, 1], SKShaderTileMode.Clamp);
                    c.DrawOval(new SKRect(-len, -width / 2, len, width / 2), p);
                    c.Restore();
                }

            using (var p = new SKPaint { IsAntialias = true })
                foreach (var (pt, r, a) in Dust)
                {
                    p.Color = new SKColor(255, 240, 200, a);
                    c.DrawCircle(pt, r, p);
                }

            DrawWand(c);

            // Ядро світла «дихає» разом із діафрагмою.
            using (var p = new SKPaint { IsAntialias = true })
            {
                float r = 120 + 40 * open;
                p.Shader = SKShader.CreateRadialGradient(Tip, r,
                    [SKColors.White, new SKColor(255, 252, 235, 255), new SKColor(255, 230, 170, 120), SKColors.Transparent], [0, 0.35f, 0.6f, 1], SKShaderTileMode.Clamp);
                c.DrawCircle(Tip, r, p);
            }

            using (var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 225) })
                foreach (var (x, y, r) in new[] { (700f, 210f, 15f), (300f, 190f, 10f), (760f, 600f, 12f), (590f, 110f, 8f), (250f, 470f, 7f), (800f, 380f, 7f), (150f, 120f, 6f), (680f, 820f, 9f) })
                {
                    using var star = new SKPath();
                    star.MoveTo(x, y - r * 2.2f);
                    star.QuadTo(x, y, x + r * 2.2f, y);
                    star.QuadTo(x, y, x, y + r * 2.2f);
                    star.QuadTo(x, y, x - r * 2.2f, y);
                    star.QuadTo(x, y, x, y - r * 2.2f);
                    star.Close();
                    c.DrawPath(star, p);
                }
        }

        /// <summary>Кільце й шість пелюсток: від кожної вершини шестикутника отвору — лінія по дотичній до кільця.</summary>
        private static void DrawAperture(SKCanvas c, float inner, float spin)
        {
            using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, StrokeCap = SKStrokeCap.Round, Color = new SKColor(255, 255, 255, 110) };
            const float r = 250;
            var v = Enumerable.Range(0, 6).Select(k =>
            {
                double a = Math.PI / 3 * k + Math.PI / 6 + spin;
                return new SKPoint(Tip.X + inner * (float)Math.Cos(a), Tip.Y + inner * (float)Math.Sin(a));
            }).ToArray();
            for (int k = 0; k < 6; k++)
            {
                SKPoint a = v[k], b = v[(k + 1) % 6];
                float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
                dx /= len; dy /= len;
                float fx = a.X - Tip.X, fy = a.Y - Tip.Y;
                float bq = fx * dx + fy * dy, cq = fx * fx + fy * fy - r * r;
                float t = -bq + MathF.Sqrt(bq * bq - cq);
                c.DrawLine(a, new SKPoint(a.X + dx * t, a.Y + dy * t), p);
            }
            p.StrokeWidth = 10;
            c.DrawCircle(Tip, r, p);
            // Друге, ледь видиме кільце — як відблиск у лінзі.
            p.StrokeWidth = 3;
            p.Color = new SKColor(200, 220, 255, 50);
            c.DrawCircle(Tip, r + 70, p);
        }

        private static void DrawWand(SKCanvas c)
        {
            using (var wand = new SKPath())
            {
                float w0 = 34, w1 = 9;
                wand.MoveTo(At(0, -w0));
                wand.LineTo(At(0.97f, -w1));
                wand.QuadTo(At(1.0f, 0), At(0.97f, w1));
                wand.LineTo(At(0, w0));
                wand.Close();
                using var p = new SKPaint { IsAntialias = true };
                p.Shader = SKShader.CreateLinearGradient(At(0.5f, -30), At(0.5f, 30),
                    [new SKColor(0x8A, 0x5A, 0x34), new SKColor(0x4A, 0x2C, 0x18), new SKColor(0x26, 0x15, 0x0B)], [0, 0.45f, 1], SKShaderTileMode.Clamp);
                c.DrawPath(wand, p);
                p.Shader = SKShader.CreateLinearGradient(At(1, 0), At(0.6f, 0), [new SKColor(255, 240, 210, 160), SKColors.Transparent], null, SKShaderTileMode.Clamp);
                c.DrawPath(wand, p);
            }
            using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 18 })
            {
                p.Color = new SKColor(0x2A, 0x17, 0x0C);
                foreach (float t in new[] { 0.16f, 0.21f }) c.DrawLine(At(t, -36), At(t, 36), p);
                p.StrokeWidth = 5;
                p.Color = new SKColor(0xB0, 0x7A, 0x48, 160);
                foreach (float t in new[] { 0.16f, 0.21f }) c.DrawLine(At(t - 0.005f, -31), At(t - 0.005f, 31), p);
            }
        }
    }
}
