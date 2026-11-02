using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lumos.Core;

/// <summary>
/// Розмиття об'єктива (як Lens Blur у Lightroom): за картою глибини від ШІ все, що далі чи ближче за точку фокуса,
/// розмивається дисками, як на відкритій діафрагмі; яскраві точки стають круглими «зайчиками» боке.
/// Рахується на полотні в лінійному світлі разом із латками — до балансу білого, експозиції й тону
/// (розмиття з ними переставне), тож результат кешується, поки тягнуть інші повзунки.
/// </summary>
public static class LensBlur
{
    public static bool Active(DevelopSettings s) => s.BlurAmount > 0 && s.DepthMap is not null;

    /// <summary>Найбільший радіус розмиття (сила 100) — частка довшої сторони кадру.</summary>
    private const double MaxRadius = 0.025;

    /// <summary>Скільки відліків на диск (спіраль «золотого кута» — рівномірно по площі).</summary>
    private const int Taps = 48;

    // Радіус розмиття на зменшеній копії не більший за цей — так навіть 24 Мп рахуються швидко.
    private const double LowResRadius = 18;

    public static void Apply(LinearImage canvas, DevelopSettings s)
    {
        if (!Active(s) || MaskRaster.Decode(s.DepthMap!) is not { } depth) return;
        int W = canvas.Width, H = canvas.Height;
        double maxCoc = s.BlurAmount / 100 * MaxRadius * Math.Max(W, H);
        if (maxCoc < 0.75) return;

        float focus = FocusDepth(depth, s);
        float half = (float)(0.02 + s.BlurRange / 100 * 0.45); // половина різкої зони (у частках глибини)
        const float ramp = 0.22f;                               // за скільки «глибини» розмиття доростає до повного

        // Сила розмиття (радіус у пікселях полотна) — за глибиною в кожній точці.
        float Coc(double u, double v)
        {
            float d = AiSegmenter.SampleFloat(depth.Data, depth.Width, depth.Height, u * depth.Width - 0.5, v * depth.Height - 0.5);
            float t = Math.Clamp((MathF.Abs(d - focus) - half) / ramp, 0, 1);
            return (float)maxCoc * t * t * (3 - 2 * t);
        }

        // Зменшена копія: множник так, щоб найбільший радіус був ≤ LowResRadius пікселів.
        int f = Math.Max(1, (int)Math.Ceiling(maxCoc / LowResRadius));
        int w = Math.Max(1, W / f), h = Math.Max(1, H / f);
        float[] src = canvas.Pixels;
        var low = new float[w * h * 3];
        var lowCoc = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int dy = 0; dy < f; dy++)
                    for (int dx = 0; dx < f; dx++)
                    {
                        int i = (Math.Min(H - 1, y * f + dy) * W + Math.Min(W - 1, x * f + dx)) * 3;
                        r += src[i]; g += src[i + 1]; b += src[i + 2];
                    }
                int o = (y * w + x) * 3;
                float norm = 1f / (f * f);
                low[o] = r * norm; low[o + 1] = g * norm; low[o + 2] = b * norm;
                lowCoc[y * w + x] = Coc((x + 0.5) / w, (y + 0.5) / h) / f;
            }
        });

        // Збирання диска: кожен відлік додає свій колір, лише якщо його власне розмиття «дотягується» сюди —
        // тож різкий предмет у фокусі не розтікається ореолом по розмитому фону. Яскраві відліки важать більше («зайчики»).
        float boost = (float)(s.BlurBokeh / 100 * 6);
        var blurred = new float[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                float radius = lowCoc[p];
                float r = low[p * 3], g = low[p * 3 + 1], b = low[p * 3 + 2], sum = 1;
                if (radius >= 0.5f)
                {
                    for (int k = 0; k < Taps; k++)
                    {
                        float dist = MathF.Sqrt((k + 0.5f) / Taps) * radius;
                        float angle = k * 2.3999632f;
                        int qx = Math.Clamp((int)MathF.Round(x + dist * MathF.Cos(angle)), 0, w - 1);
                        int qy = Math.Clamp((int)MathF.Round(y + dist * MathF.Sin(angle)), 0, h - 1);
                        int q = qy * w + qx;
                        float cover = Math.Clamp(lowCoc[q] - dist + 1, 0, 1);
                        if (cover <= 0) continue;
                        float lum = 0.2126f * low[q * 3] + 0.7152f * low[q * 3 + 1] + 0.0722f * low[q * 3 + 2];
                        float weight = cover * (1 + boost * Smooth(0.45f, 1f, lum));
                        r += low[q * 3] * weight; g += low[q * 3 + 1] * weight; b += low[q * 3 + 2] * weight;
                        sum += weight;
                    }
                }
                blurred[p * 3] = r / sum; blurred[p * 3 + 1] = g / sum; blurred[p * 3 + 2] = b / sum;
            }
        });

        // Назад на повне полотно: де розмиття менше пікселя — лишається оригінал (повна різкість), далі — розмите.
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float coc = Coc((x + 0.5) / W, (y + 0.5) / H);
                float a = Math.Clamp(coc - 0.5f, 0, 1);
                if (a <= 0) continue;
                double lx = (x + 0.5) / f - 0.5, ly = (y + 0.5) / f - 0.5;
                int i = (y * W + x) * 3;
                for (int c = 0; c < 3; c++)
                    src[i + c] += (Sample(blurred, w, h, lx, ly, c) - src[i + c]) * a;
            }
        });
    }

    /// <summary>Глибина точки фокуса: у вибраній точці (середнє довкола) або, якщо не вибрано, — найближче в кадрі.</summary>
    private static float FocusDepth(MaskRaster.Decoded depth, DevelopSettings s)
    {
        if (s.BlurFocusX >= 0 && s.BlurFocusY >= 0)
        {
            float sum = 0;
            int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++, n++)
                    sum += AiSegmenter.SampleFloat(depth.Data, depth.Width, depth.Height,
                        s.BlurFocusX * depth.Width - 0.5 + dx, s.BlurFocusY * depth.Height - 0.5 + dy);
            return sum / n;
        }
        var sorted = (float[])depth.Data.Clone();
        Array.Sort(sorted);
        return sorted[(int)(sorted.Length * 0.97)];
    }

    /// <summary>
    /// Точка фокуса за замовчуванням — головний об'єкт кадру (як у Lightroom): маска «Об'єкт» від ШІ, її центр
    /// (а якщо центр випав за межі предмета, як у кільця, — найближча точка всередині). null — моделі немає чи об'єкта не знайдено.
    /// </summary>
    public static (double X, double Y)? SuggestFocus(byte[] bgra, int width, int height)
    {
        if (!AiModels.IsInstalled(AiModels.Subject)) return null;
        var subject = AiSegmenter.Segment(AiModels.Subject, bgra, width, height);
        if (subject.Peak < 0.5f) return null;
        double sx = 0, sy = 0, sum = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float m = subject.Mask[y * width + x];
                if (m < 0.5f) continue;
                sx += x * m; sy += y * m; sum += m;
            }
        if (sum <= 0) return null;
        int cx = (int)(sx / sum), cy = (int)(sy / sum);
        if (subject.Mask[cy * width + cx] < 0.5f)
        {
            double best = double.MaxValue;
            int bx = cx, by = cy; // найближча точка — до центру мас, а не до попередньої знайденої
            for (int y = 0; y < height; y += 2)
                for (int x = 0; x < width; x += 2)
                {
                    if (subject.Mask[y * width + x] < 0.5f) continue;
                    double d = (x - cx) * (double)(x - cx) + (y - cy) * (double)(y - cy);
                    if (d < best) (best, bx, by) = (d, x, y);
                }
            (cx, cy) = (bx, by);
        }
        return ((cx + 0.5) / width, (cy + 0.5) / height);
    }

    public sealed record DepthResult(string Raster, bool Gpu, long Milliseconds);

    /// <summary>
    /// Карта глибини для кадру «як є» (8 біт sRGB): мережа на коротшій стороні 518 пікселів, потім нормалізація
    /// (1 — найближче, 0 — найдальше) і підгонка країв під фото керованим фільтром, щоб межа предмета й фону була точною.
    /// </summary>
    public static DepthResult EstimateDepth(byte[] bgra, int width, int height)
    {
        var watch = Stopwatch.StartNew();
        double scale = 518.0 / Math.Min(width, height);
        int mw = Math.Max(14, (int)Math.Round(width * scale / 14) * 14), mh = Math.Max(14, (int)Math.Round(height * scale / 14) * 14);

        float[] mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
        var input = new DenseTensor<float>([1, 3, mh, mw]);
        Parallel.For(0, mh, y =>
        {
            for (int x = 0; x < mw; x++)
            {
                double sx = (x + 0.5) * width / mw - 0.5, sy = (y + 0.5) * height / mh - 0.5;
                for (int c = 0; c < 3; c++)
                    input[0, c, y, x] = (SampleBgra(bgra, width, height, sx, sy, 2 - c) / 255f - mean[c]) / std[c];
            }
        });
        float[] output = AiSegmenter.Run(AiModels.Depth, [NamedOnnxValue.CreateFromTensor("pixel_values", input)], out bool gpu);

        // Нормалізація без крайніх 1 %: поодинокі викиди не стискають решту глибини.
        var sorted = (float[])output.Clone();
        Array.Sort(sorted);
        float lo = sorted[(int)(sorted.Length * 0.01)], hi = sorted[(int)(sorted.Length * 0.99)];
        float range = Math.Max(1e-6f, hi - lo);

        // До розміру кадру, підгонка країв — і в PNG (≤ 1024 по довшій стороні).
        var depth = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float v = AiSegmenter.SampleFloat(output, mw, mh, (x + 0.5) * mw / width - 0.5, (y + 0.5) * mh / height - 0.5);
                depth[y * width + x] = Math.Clamp((v - lo) / range, 0, 1);
            }
        });
        AiSegmenter.Refine(depth, bgra, width, height);
        return new DepthResult(MaskRaster.Encode(depth, width, height), gpu, watch.ElapsedMilliseconds);
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
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

    private static float SampleBgra(byte[] bgra, int w, int h, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = bgra[(y0 * w + x0) * 4 + channel], b = bgra[(y0 * w + x1) * 4 + channel];
        float c = bgra[(y1 * w + x0) * 4 + channel], d = bgra[(y1 * w + x1) * 4 + channel];
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }
}
