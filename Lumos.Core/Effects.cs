using System.Buffers;

namespace Lumos.Core;

/// <summary>
/// Що саме рахуємо: <see cref="Scale"/> — скільки пікселів зображення на один піксель оригіналу
/// (прев'ю 2048 з 6024 → ≈0.34, експорт і масштаб 100 % → 1); <see cref="FullLongSide"/> — довша сторона оригіналу.
/// Радіуси ефектів задані в пікселях оригіналу, тож прев'ю й експорт виглядають однаково.
/// Зсув і розмір кадру — для частини зображення (масштаб 100 %): віньєтка й зерно рахуються від усього кадру.
/// </summary>
public sealed record RenderFrame(double Scale, int FullLongSide, int OffsetX = 0, int OffsetY = 0, int FrameWidth = 0, int FrameHeight = 0)
{
    public static RenderFrame For(LinearImage image, double scale, int fullLongSide) =>
        new(scale, fullLongSide, 0, 0, image.Width, image.Height);
}

/// <summary>
/// Ефекти, яким потрібні сусідні пікселі: шумозаглушення, видалення димки, чіткість, текстура, різкість.
/// Працюють у лінійному світлі після балансу білого, але до експозиції й тонової кривої —
/// тож результат можна кешувати, поки тягнуть інші повзунки.
/// Чіткість/текстура/різкість змінюють лише яскравість (через логарифм яскравості), тож кольори не «пливуть».
/// </summary>
internal static class SpatialEffects
{
    public static bool Active(DevelopSettings s) =>
        s.Texture != 0 || s.Clarity != 0 || s.Dehaze != 0 || s.SharpenAmount != 0
        || s.NoiseReduction != 0 || s.ColorNoiseReduction != 0;

    public static void Apply(float[] rgb, int w, int h, DevelopSettings s, RenderFrame frame)
    {
        int n = w * h;
        var pool = ArrayPool<float>.Shared;
        float[] a = pool.Rent(n), b = pool.Rent(n), c = pool.Rent(n), tmp = pool.Rent(n);
        try
        {
            double px = frame.Scale;                    // 1 піксель оригіналу в пікселях цього зображення
            double longSide = frame.FullLongSide * px;  // довша сторона в пікселях цього зображення

            if (s.ColorNoiseReduction > 0) ColorNoise(rgb, n, w, h, s.ColorNoiseReduction / 100 * 6 * px, a, b, c, tmp);
            if (s.NoiseReduction > 0) LuminanceNoise(rgb, n, w, h, s.NoiseReduction / 100, Math.Max(1, (int)Math.Round(3 * px)), a, b, c, tmp);
            if (s.Dehaze != 0) Dehaze(rgb, n, w, h, (float)(s.Dehaze / 100), 0.01 * longSide, a, tmp);
            if (s.Clarity != 0 || s.Texture != 0 || s.SharpenAmount != 0) LocalContrast(rgb, n, w, h, s, px, longSide, a, b, c, tmp);
        }
        finally
        {
            pool.Return(a);
            pool.Return(b);
            pool.Return(c);
            pool.Return(tmp);
        }
    }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    /// <summary>Кольоровий шум: розмиваємо лише різницю кольорів (R−Y, B−Y), яскравість і різкість лишаються.</summary>
    private static void ColorNoise(float[] rgb, int n, int w, int h, double sigma, float[] y, float[] cr, float[] cb, float[] tmp)
    {
        if (sigma < 0.3) return;
        Parallel.For(0, n, i =>
        {
            float l = Luma(rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2]);
            y[i] = l;
            cr[i] = rgb[3 * i] - l;
            cb[i] = rgb[3 * i + 2] - l;
        });
        Blur.Gaussian(cr, tmp, w, h, sigma);
        Blur.Gaussian(cb, tmp, w, h, sigma);
        Parallel.For(0, n, i =>
        {
            float r = y[i] + cr[i], b = y[i] + cb[i];
            float g = (y[i] - 0.2126f * r - 0.0722f * b) / 0.7152f;
            rgb[3 * i] = MathF.Max(0, r);
            rgb[3 * i + 1] = MathF.Max(0, g);
            rgb[3 * i + 2] = MathF.Max(0, b);
        });
    }

    /// <summary>
    /// Шум яскравості: керований фільтр (guided filter) — згладжує рівні ділянки, але зберігає краї.
    /// Рахується на √яскравості, де шум у тінях і світлих приблизно однаковий.
    /// </summary>
    private static void LuminanceNoise(float[] rgb, int n, int w, int h, double amount, int r, float[] p, float[] mean, float[] mean2, float[] tmp)
    {
        float eps = (float)(amount * amount * 0.004);
        Parallel.For(0, n, i =>
        {
            float v = MathF.Sqrt(MathF.Max(0, Luma(rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2])));
            p[i] = v;
            mean[i] = v;
            mean2[i] = v * v;
        });
        Blur.Box(mean, tmp, w, h, r);
        Blur.Box(mean2, tmp, w, h, r);
        // a, b фільтра пишемо на місце mean/mean2, далі теж усереднюємо.
        Parallel.For(0, n, i =>
        {
            float m = mean[i], variance = MathF.Max(0, mean2[i] - m * m);
            float k = variance / (variance + eps);
            mean[i] = k;
            mean2[i] = m - k * m;
        });
        Blur.Box(mean, tmp, w, h, r);
        Blur.Box(mean2, tmp, w, h, r);
        Parallel.For(0, n, i =>
        {
            float before = p[i];
            float after = MathF.Max(0, mean[i] * before + mean2[i]);
            float ratio = before > 1e-4f ? after * after / (before * before) : 1;
            rgb[3 * i] *= ratio;
            rgb[3 * i + 1] *= ratio;
            rgb[3 * i + 2] *= ratio;
        });
    }

    /// <summary>
    /// Видалення димки за «темним каналом»: у чистому повітрі в кожній ділянці є щось темне в якомусь каналі;
    /// димка цей мінімум піднімає. Оцінюємо «прозорість» повітря й прибираємо підняте. Мінус — додає димку.
    /// </summary>
    private static void Dehaze(float[] rgb, int n, int w, int h, float amount, double sigma, float[] dark, float[] tmp)
    {
        Parallel.For(0, n, i => dark[i] = Math.Clamp(MathF.Min(rgb[3 * i], MathF.Min(rgb[3 * i + 1], rgb[3 * i + 2])), 0, 1));
        Blur.Gaussian(dark, tmp, w, h, sigma);
        const float air = 1f; // колір «повітря» — білий
        Parallel.For(0, n, i =>
        {
            for (int c = 0; c < 3; c++)
            {
                float v = rgb[3 * i + c];
                if (amount > 0)
                {
                    float t = MathF.Max(0.2f, 1 - amount * 0.85f * dark[i]);
                    v = (v - air * (1 - t)) / t;
                }
                else
                {
                    v += (0.6f * air - v) * -amount * 0.5f;
                }
                rgb[3 * i + c] = MathF.Max(0, v);
            }
        });
    }

    /// <summary>
    /// Чіткість, текстура й різкість — підсилення різниці між яскравістю пікселя та її розмиттям (у логарифмі):
    /// чіткість — великий радіус і середні тони, текстура — середні дрібні деталі, різкість — найдрібніші.
    /// </summary>
    private static void LocalContrast(float[] rgb, int n, int w, int h, DevelopSettings s, double px, double longSide,
        float[] logY, float[] blurA, float[] blurB, float[] tmp)
    {
        const float eps = 1e-4f, midGrey = -2.47f; // log2(0.18)
        Parallel.For(0, n, i => logY[i] = MathF.Log2(Luma(rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2]) + eps));

        var delta = ArrayPool<float>.Shared.Rent(n);
        try
        {
            Array.Clear(delta, 0, n);

            if (s.Clarity != 0)
            {
                Array.Copy(logY, blurA, n);
                Blur.Gaussian(blurA, tmp, w, h, 0.012 * longSide);
                float k = (float)(s.Clarity / 100 * 0.55);
                Parallel.For(0, n, i =>
                {
                    float d = Math.Clamp(logY[i] - blurA[i], -2, 2);
                    float mid = (logY[i] - midGrey) / 2.5f;
                    delta[i] += k * d * MathF.Exp(-mid * mid); // найсильніше в середніх тонах
                });
            }

            if (s.Texture != 0)
            {
                // Смуга деталей між дуже дрібним (шум) і великим (форми): різниця двох розмиттів.
                Array.Copy(logY, blurA, n);
                Array.Copy(logY, blurB, n);
                Blur.Gaussian(blurA, tmp, w, h, Math.Max(0.6 * px, 0.0008 * longSide));
                Blur.Gaussian(blurB, tmp, w, h, 0.004 * longSide);
                float k = (float)(s.Texture / 100 * 0.8);
                Parallel.For(0, n, i => delta[i] += k * Math.Clamp(blurA[i] - blurB[i], -1.5f, 1.5f));
            }

            if (s.SharpenAmount != 0)
                Sharpen(logY, delta, n, w, h, s, px, blurA, blurB, tmp);

            Parallel.For(0, n, i =>
            {
                float f = MathF.Pow(2f, delta[i]);
                rgb[3 * i] *= f;
                rgb[3 * i + 1] *= f;
                rgb[3 * i + 2] *= f;
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(delta);
        }
    }

    /// <summary>
    /// Різкість (нерізке маскування): радіус у пікселях оригіналу, «деталізація» — наскільки підсилювати
    /// великі перепади (менше — менше ореолів), «маскування» — лише на краях, рівні ділянки (небо, шкіра) не чіпати.
    /// </summary>
    private static void Sharpen(float[] logY, float[] delta, int n, int w, int h, DevelopSettings s, double px,
        float[] blur, float[] mask, float[] tmp)
    {
        double sigma = s.SharpenRadius * px;
        if (sigma < 0.25) return; // на дуже зменшеному прев'ю різкість фізично не видно — її показує масштаб 100 %
        Array.Copy(logY, blur, n);
        Blur.Gaussian(blur, tmp, w, h, sigma);

        float amount = (float)(s.SharpenAmount / 100 * 1.4);
        float halo = (float)((1 - s.SharpenDetail / 100) * 6);
        float threshold = (float)(s.SharpenMasking / 100 * 0.35);
        if (threshold > 0)
        {
            // Сила краю — перепад розмитої яскравості до сусідів.
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float gx = blur[y * w + Math.Min(x + 1, w - 1)] - blur[y * w + Math.Max(x - 1, 0)];
                    float gy = blur[Math.Min(y + 1, h - 1) * w + x] - blur[Math.Max(y - 1, 0) * w + x];
                    mask[i] = MathF.Sqrt(gx * gx + gy * gy);
                }
            });
            Blur.Gaussian(mask, tmp, w, h, Math.Max(1, sigma));
        }

        Parallel.For(0, n, i =>
        {
            float d = logY[i] - blur[i];
            d /= 1 + MathF.Abs(d) * halo;
            float m = threshold > 0 ? Smooth(threshold * 0.5f, threshold * 1.5f, mask[i]) : 1;
            delta[i] += amount * d * m;
        });
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>
/// Віньєтка після обрізки: кути темніші (мінус) чи світліші (плюс). Рахується від усього кадру,
/// тож у масштабі 100 % (частина кадру) виглядає так само, як на повному.
/// </summary>
internal sealed class Vignette
{
    internal readonly float _amount, _inner, _outer, _power, _aspectX, _aspectY, _cx, _cy;

    public static Vignette? Create(DevelopSettings s, RenderFrame frame, int width, int height) =>
        s.VignetteAmount == 0 ? null : new Vignette(s, frame, width, height);

    private Vignette(DevelopSettings s, RenderFrame frame, int width, int height)
    {
        float fw = frame.FrameWidth > 0 ? frame.FrameWidth : width, fh = frame.FrameHeight > 0 ? frame.FrameHeight : height;
        _amount = (float)(s.VignetteAmount / 100 * 1.6);
        float mid = (float)(0.25 + s.VignetteMidpoint / 100 * 0.9);
        float feather = (float)(0.05 + s.VignetteFeather / 100 * 0.9);
        _inner = mid * (1 - feather * 0.8f);
        _outer = _inner + 0.15f + feather;
        // Округлість: 0 — еліпс за пропорціями кадру, +100 — коло, −100 — майже прямокутник.
        float round = (float)(s.VignetteRoundness / 100);
        _power = round < 0 ? 2 - round * 6 : 2;
        float circle = Math.Max(0, round), maxSide = Math.Max(fw, fh);
        _aspectX = 1 + (fw / maxSide - 1) * circle;
        _aspectY = 1 + (fh / maxSide - 1) * circle;
        _cx = fw / 2 - frame.OffsetX;
        _cy = fh / 2 - frame.OffsetY;
        _halfW = fw / 2;
        _halfH = fh / 2;
    }

    internal readonly float _halfW, _halfH;

    /// <summary>Множник яскравості для пікселя (x, y) цього зображення.</summary>
    public float Factor(int x, int y)
    {
        float nx = MathF.Abs((x + 0.5f - _cx) / _halfW * _aspectX), ny = MathF.Abs((y + 0.5f - _cy) / _halfH * _aspectY);
        float d = _power == 2
            ? MathF.Sqrt(nx * nx + ny * ny)
            : MathF.Pow(MathF.Pow(nx, _power) + MathF.Pow(ny, _power), 1 / _power);
        if (d <= _inner) return 1; // середина кадру — без змін
        float t = Math.Min(1, (d - _inner) / (_outer - _inner));
        t = t * t * (3 - 2 * t);
        return MathF.Exp(_amount * t * 0.6931472f);
    }
}

/// <summary>
/// Плівкове зерно: шум, прив'язаний до пікселів оригіналу (тож однаковий у прев'ю, масштабі 100 % і експорті),
/// найсильніший у середніх тонах. Розмір — величина зернинок, шорсткість — додаткове дрібне зерно.
/// </summary>
internal sealed class Grain
{
    internal readonly float _amount, _cell, _rough, _scale, _ox, _oy;

    public static Grain? Create(DevelopSettings s, RenderFrame frame) => s.GrainAmount <= 0 ? null : new Grain(s, frame);

    private Grain(DevelopSettings s, RenderFrame frame)
    {
        _scale = (float)frame.Scale;
        _cell = (float)(0.8 + s.GrainSize / 100 * 3.2); // у пікселях оригіналу
        _rough = (float)(s.GrainRoughness / 100);
        // На зменшеному прев'ю кілька зернинок зливаються в один піксель — зерно там слабше, як і насправді.
        _amount = (float)(s.GrainAmount / 100 * 0.12 * Math.Min(1, _cell * _scale));
        _ox = frame.OffsetX;
        _oy = frame.OffsetY;
    }

    public float Apply(float v, int x, int y)
    {
        float u = (x + _ox) / _scale / _cell, w = (y + _oy) / _scale / _cell;
        float noise = Value(u, w) * (1 - 0.5f * _rough) + Value(u * 2.3f + 17, w * 2.3f + 31) * 0.5f * _rough;
        float mid = Math.Clamp(v, 0, 1);
        return v + _amount * noise * (0.25f + 3 * mid * (1 - mid));
    }

    private static float Value(float u, float v)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float tx = u - x0, ty = v - y0;
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        float a = Hash(x0, y0), b = Hash(x0 + 1, y0), c = Hash(x0, y0 + 1), d = Hash(x0 + 1, y0 + 1);
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    private static float Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF * 2 - 1;
        }
    }
}
