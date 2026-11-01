using System.Diagnostics;
using System.Runtime.CompilerServices;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lumos.Core;

/// <summary>
/// ШІ-видалення: нейромережа LaMa домальовує фон замість обведеного предмета.
/// Береться квадратна ділянка навколо мазка з запасом контексту, зводиться до 512×512, мережа домальовує дірку,
/// і результат зберігається в латці (PNG) — щоб не запускати мережу при кожному перерахунку й у файлі правок.
/// Лінійні значення кодуються в картинку з множником (світле не обрізається), а при накладанні декодуються назад,
/// тож латка проходить увесь конвеєр правок разом із фото.
/// </summary>
public static class Inpainter
{
    public const int Side = 512;

    public sealed record Result(HealSpot Spot, bool Gpu, long Milliseconds);

    /// <param name="canvas">Полотно (вирівняне, до обрізки) з уже накладеними попередніми латками; що більша роздільність — то детальніша латка.</param>
    public static Result Generate(LinearImage canvas, HealSpot spot)
    {
        var watch = Stopwatch.StartNew();
        int W = canvas.Width, H = canvas.Height;
        double hard = Math.Max(1, spot.Size * Math.Max(W, H) / 2);
        double radius = Healing.OuterRadius(spot, W, H);
        var dabs = MaskShapes.Dabs(spot.Points, W, H, Math.Max(1, hard * 0.25));

        // Ділянка: предмет + контекст навколо (мережі треба бачити, що довкола, щоб продовжити фон).
        double x0 = dabs.Min(d => d.X) - radius, x1 = dabs.Max(d => d.X) + radius;
        double y0 = dabs.Min(d => d.Y) - radius, y1 = dabs.Max(d => d.Y) + radius;
        double size = Math.Max(x1 - x0, y1 - y0);
        double side = size + 2 * Math.Max(size * 0.6, 24);
        double rw = Math.Min(side, W), rh = Math.Min(side, H);
        double rx = Math.Clamp((x0 + x1) / 2 - rw / 2, 0, W - rw), ry = Math.Clamp((y0 + y1) / 2 - rh / 2, 0, H - rh);

        // Ділянка → 512×512 (при зменшенні — середнє кількох відліків, щоб не було «сходинок»).
        var linear = new float[Side * Side * 3];
        double fx = rw / Side, fy = rh / Side;
        int n = Math.Clamp((int)Math.Ceiling(Math.Max(fx, fy)), 1, 4);
        Parallel.For(0, Side, v =>
        {
            for (int u = 0; u < Side; u++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int sy = 0; sy < n; sy++)
                        for (int sx = 0; sx < n; sx++)
                            acc += Sample(canvas.Pixels, W, H, rx + (u + (sx + 0.5) / n) * fx - 0.5, ry + (v + (sy + 0.5) / n) * fy - 0.5, c);
                    linear[(v * Side + u) * 3 + c] = acc / (n * n);
                }
        });

        // Множник: найсвітліше (без 0,5 % викидів) — майже біле; так і темні кадри мережа бачить нормально.
        var brightest = new float[Side * Side];
        for (int i = 0; i < brightest.Length; i++) brightest[i] = Math.Max(linear[3 * i], Math.Max(linear[3 * i + 1], linear[3 * i + 2]));
        Array.Sort(brightest);
        float scale = 0.95f / Math.Max(brightest[(int)(brightest.Length * 0.995)], 1e-4f);

        // Маска дірки в 512×512: слід пензля (з м'яким краєм) трохи розширений — мережа має перемалювати й край предмета.
        int pw = (int)Math.Ceiling(rw), ph = (int)Math.Ceiling(rh), px0 = (int)rx, py0 = (int)ry;
        var footprint = Healing.Footprint(dabs, radius, hard, px0, py0, pw, ph);
        var hole = new float[Side * Side];
        Parallel.For(0, Side, v =>
        {
            for (int u = 0; u < Side; u++)
            {
                int sx = Math.Clamp((int)(rx + (u + 0.5) * fx) - px0, 0, pw - 1), sy = Math.Clamp((int)(ry + (v + 0.5) * fy) - py0, 0, ph - 1);
                hole[v * Side + u] = footprint[sy * pw + sx] > 0.02f ? 1 : 0;
            }
        });
        hole = Dilate(hole, 4);

        var image = new DenseTensor<float>([1, 3, Side, Side]);
        var mask = new DenseTensor<float>([1, 1, Side, Side]);
        for (int v = 0; v < Side; v++)
            for (int u = 0; u < Side; u++)
            {
                int i = v * Side + u;
                for (int c = 0; c < 3; c++) image[0, c, v, u] = EncodeSrgb(Math.Clamp(linear[3 * i + c] * scale, 0, 1));
                mask[0, 0, v, u] = hole[i];
            }

        float[] output = AiSegmenter.Run(AiModels.Inpaint,
            [NamedOnnxValue.CreateFromTensor("image", image), NamedOnnxValue.CreateFromTensor("mask", mask)], out bool gpu);

        // Латка — те, що намалювала мережа (RGB 0…255), у PNG.
        var rgb = new byte[Side * Side * 3];
        for (int v = 0; v < Side; v++)
            for (int u = 0; u < Side; u++)
                for (int c = 0; c < 3; c++)
                    rgb[(v * Side + u) * 3 + c] = (byte)Math.Clamp(output[(c * Side + v) * Side + u] + 0.5f, 0, 255);
        using var png = new MagickImage(rgb, new PixelReadSettings(Side, Side, StorageType.Char, PixelMapping.RGB));
        png.Format = MagickFormat.Png;

        var result = spot with
        {
            Mode = HealMode.Remove,
            Patch = png.ToBase64(),
            PatchX = rx / W, PatchY = ry / H, PatchW = rw / W, PatchH = rh / H,
            PatchScale = scale,
            SourceDX = 0, SourceDY = 0,
        };
        return new Result(result, gpu, watch.ElapsedMilliseconds);
    }

    // Розкодовані латки (лінійні значення 512×512×3) — за рядком PNG, щоб не розпаковувати при кожному кадрі.
    private static readonly ConditionalWeakTable<string, float[]> Decoded = [];

    /// <summary>Накладає домальовану латку там, де пройшов пензель (з м'яким краєм).</summary>
    internal static void Blend(LinearImage img, HealSpot spot, float[] weight, int bx0, int by0, int bw, int bh, float opacity)
    {
        if (spot.Patch is not { } patch || spot.PatchW <= 0 || spot.PatchH <= 0) return;
        float scale = (float)spot.PatchScale;
        var linear = Decoded.GetValue(patch, p =>
        {
            using var image = new MagickImage(Convert.FromBase64String(p));
            var bytes = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = DecodeSrgb(i / 255f) / scale;
            var values = new float[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) values[i] = lut[bytes[i]];
            return values;
        });

        int W = img.Width, H = img.Height;
        float[] px = img.Pixels;
        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                float a = weight[y * bw + x] * opacity;
                if (a <= 0) continue;
                int X = bx0 + x, Y = by0 + y;
                double u = ((X + 0.5) / W - spot.PatchX) / spot.PatchW * Side - 0.5;
                double v = ((Y + 0.5) / H - spot.PatchY) / spot.PatchH * Side - 0.5;
                if (u < -1 || v < -1 || u > Side || v > Side) continue;
                int d = (Y * W + X) * 3;
                for (int c = 0; c < 3; c++)
                    px[d + c] += (Sample(linear, Side, Side, u, v, c) - px[d + c]) * a;
            }
        });
    }

    private static float[] Dilate(float[] mask, int r)
    {
        var result = new float[mask.Length];
        Parallel.For(0, Side, y =>
        {
            for (int x = 0; x < Side; x++)
            {
                float m = 0;
                for (int dy = -r; dy <= r && m == 0; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= Side || sy >= Side || dx * dx + dy * dy > r * r) continue;
                        if (mask[sy * Side + sx] > 0) { m = 1; break; }
                    }
                result[y * Side + x] = m;
            }
        });
        return result;
    }

    private static float Sample(float[] px, int w, int h, double x, double y, int c)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = px[(y0 * w + x0) * 3 + c], b = px[(y0 * w + x1) * 3 + c];
        float d = px[(y1 * w + x0) * 3 + c], e = px[(y1 * w + x1) * 3 + c];
        return a + (b - a) * tx + (d - a) * ty + (a - b - d + e) * tx * ty;
    }

    private static float EncodeSrgb(float x) => x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;

    private static float DecodeSrgb(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
}
