using SkiaSharp;
// Заставка Lumos (права половина вікна запуску): нічне небо, паличка, «Люмос!» — світло в кільці діафрагми, іскри й зоряний пил.
string outFile = args[0];
const int W = 860, H = 1000;
using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
var c = surface.Canvas;

using (var p = new SKPaint { IsAntialias = true })
{
    p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
        [new SKColor(0x22, 0x1C, 0x52), new SKColor(0x0D, 0x10, 0x2A), new SKColor(0x04, 0x05, 0x10)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
    c.DrawRect(0, 0, W, H, p);
}

var rnd = new Random(7);
// Далекі зорі.
using (var p = new SKPaint { IsAntialias = true })
    for (int i = 0; i < 260; i++)
    {
        float x = rnd.NextSingle() * W, y = rnd.NextSingle() * H, r = 0.6f + rnd.NextSingle() * 1.6f;
        p.Color = new SKColor(220, 228, 255, (byte)(60 + rnd.Next(150)));
        c.DrawCircle(x, y, r, p);
    }

var tip = new SKPoint(500, 400);

// Велике м'яке сяйво.
using (var p = new SKPaint { IsAntialias = true })
{
    p.Shader = SKShader.CreateRadialGradient(tip, 640,
        [new SKColor(255, 250, 230, 200), new SKColor(170, 215, 255, 110), new SKColor(90, 110, 255, 40), SKColors.Transparent],
        [0, 0.16f, 0.5f, 1], SKShaderTileMode.Clamp);
    c.DrawCircle(tip, 640, p);
}

// Кільце діафрагми.
using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, StrokeCap = SKStrokeCap.Round, Color = new SKColor(255, 255, 255, 110) })
{
    float r = 250, ri = 108;
    var v = Enumerable.Range(0, 6).Select(k =>
    {
        double a = Math.PI / 3 * k + Math.PI / 6;
        return new SKPoint(tip.X + ri * (float)Math.Cos(a), tip.Y + ri * (float)Math.Sin(a));
    }).ToArray();
    for (int k = 0; k < 6; k++)
    {
        SKPoint a = v[k], b = v[(k + 1) % 6];
        float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        dx /= len; dy /= len;
        float fx = a.X - tip.X, fy = a.Y - tip.Y;
        float bq = fx * dx + fy * dy, cq = fx * fx + fy * fy - r * r;
        float t = -bq + MathF.Sqrt(bq * bq - cq);
        c.DrawLine(a, new SKPoint(a.X + dx * t, a.Y + dy * t), p);
    }
    p.StrokeWidth = 10;
    c.DrawCircle(tip, r, p);
    // Друге, ледь видиме кільце — як відблиск у лінзі.
    p.StrokeWidth = 3;
    p.Color = new SKColor(200, 220, 255, 50);
    c.DrawCircle(tip, r + 70, p);
}

// Промені.
using (var p = new SKPaint { IsAntialias = true })
    foreach (var (angle, len, width) in new[] { (0f, 520f, 16f), (90f, 520f, 16f), (45f, 260f, 8f), (135f, 260f, 8f) })
    {
        c.Save();
        c.Translate(tip.X, tip.Y);
        c.RotateDegrees(angle);
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(-len, 0), new SKPoint(len, 0),
            [SKColors.Transparent, new SKColor(255, 255, 255, 230), SKColors.Transparent], [0, 0.5f, 1], SKShaderTileMode.Clamp);
        c.DrawOval(new SKRect(-len, -width / 2, len, width / 2), p);
        c.Restore();
    }

// Паличка з лівого нижнього кута.
var start = new SKPoint(60, 1040);
var dir = new SKPoint(tip.X - start.X, tip.Y - start.Y);
float length = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
var u = new SKPoint(dir.X / length, dir.Y / length);
var n = new SKPoint(-u.Y, u.X);
SKPoint At(float t, float side) => new(start.X + dir.X * t + n.X * side, start.Y + dir.Y * t + n.Y * side);

// Світловий слід уздовж палички — іскристий пил.
using (var p = new SKPaint { IsAntialias = true })
    for (int i = 0; i < 140; i++)
    {
        float t = 0.35f + rnd.NextSingle() * 0.65f, side = (rnd.NextSingle() - 0.5f) * 220 * (1.1f - t);
        var pt = At(t, side);
        p.Color = new SKColor(255, 240, 200, (byte)(40 + rnd.Next(170) * t));
        c.DrawCircle(pt, 0.8f + rnd.NextSingle() * 2.2f, p);
    }

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

// Ядро світла.
using (var p = new SKPaint { IsAntialias = true })
{
    p.Shader = SKShader.CreateRadialGradient(tip, 140,
        [SKColors.White, new SKColor(255, 252, 235, 255), new SKColor(255, 230, 170, 120), SKColors.Transparent], [0, 0.35f, 0.6f, 1], SKShaderTileMode.Clamp);
    c.DrawCircle(tip, 140, p);
}

// Великі іскри-зірочки.
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

using var image = surface.Snapshot();
using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
File.WriteAllBytes(outFile, data.ToArray());
Console.WriteLine("done");
