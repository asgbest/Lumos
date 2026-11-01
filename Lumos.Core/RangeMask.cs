namespace Lumos.Core;

/// <summary>Вибраний піпеткою колір для маски за кольором: відтінок і насиченість (a, b в Oklab, поділені на яскравість).</summary>
public readonly record struct RangeColor(double A, double B);

/// <summary>
/// Як маска «бачить» фото: з експозицією (загальною + базовою для RAW) і базовою тоновою кривою, тож яскравість 0…100
/// відповідає тому, що на екрані без інших правок.
/// </summary>
public readonly record struct RangeLook(float Gain, ToneProfile Profile)
{
    public static RangeLook For(DevelopSettings s, ToneProfile profile) =>
        new((float)Math.Pow(2, s.Exposure + profile.BaseExposureEv), profile);
}

/// <summary>
/// Маска за кольором і яскравістю (як «Range Mask» у Lightroom): сила в кожному пікселі залежить від того,
/// наскільки його колір схожий на вибрані піпеткою і чи його яскравість у заданих межах.
/// Рахується з фото до правок (лише з експозицією і базовою кривою, див. <see cref="RangeLook"/>), тож маска не «пливе»,
/// коли тягнеш інші повзунки.
/// Колір порівнюється без яскравості, тож освітлене й затінене небо — однаково «блакитні».
/// Наприкінці трохи розмивається, щоб не було «рваних» країв і шуму.
/// </summary>
public static class RangeMask
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ToneProfile, float[]> Tones = new();

    /// <summary>Базова тонова крива (повзунки на нулях) — яскравість на екрані для лінійного значення.</summary>
    private static float[] Tone(ToneProfile profile) => Tones.GetOrAdd(profile, p => Developer.BuildToneLuts(new DevelopSettings(), p).G);

    /// <summary>Яскравість (0…100, як на екрані) і колір пікселя — те, з чим порівнює маска.</summary>
    private static (double Lightness, RangeColor Color) Measure(float r, float g, float b, float gain, float[] tone)
    {
        r = MathF.Max(0, r * gain); g = MathF.Max(0, g * gain); b = MathF.Max(0, b * gain);
        float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        const int last = Developer.LutSize - 1;
        double lightness = 100 * tone[Math.Min(last, (int)(MathF.Sqrt(y / Developer.LutMax) * last + 0.5f))];

        // Oklab (Björn Ottosson): з лінійного sRGB.
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        double B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        // Ділимо на яскравість — колір не залежить від освітлення; +0.08 — щоб шум у глибоких тінях не давав «кольору».
        double k = 1 / (L + 0.08);
        return (lightness, new RangeColor(A * k, B * k));
    }

    /// <summary>Середнє по квадрату 5×5 довкола пікселя (x, y) — для піпетки.</summary>
    public static (double Lightness, RangeColor Color)? Sample(LinearImage image, int x, int y, RangeLook look)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int yy = Math.Max(0, y - 2); yy <= Math.Min(image.Height - 1, y + 2); yy++)
            for (int xx = Math.Max(0, x - 2); xx <= Math.Min(image.Width - 1, x + 2); xx++)
            {
                int i = (yy * image.Width + xx) * 3;
                r += image.Pixels[i]; g += image.Pixels[i + 1]; b += image.Pixels[i + 2];
                n++;
            }
        return Measure((float)(r / n), (float)(g / n), (float)(b / n), look.Gain, Tone(look.Profile));
    }

    /// <summary>Сила обмеження в кожному пікселі (0…1); null — маска нічим не обмежена.</summary>
    public static float[]? Weights(Mask mask, LinearImage image, RangeLook look, RenderFrame frame)
    {
        if (!mask.HasRange) return null;
        float gain = look.Gain;
        float[] tone = Tone(look.Profile);
        int w = image.Width, h = image.Height;
        var result = new float[w * h];
        var colors = mask.RangeColors.ToArray();
        // Допуск: 0 — лише майже той самий колір, 100 — увесь схожий діапазон відтінків.
        double reach = 0.012 + Math.Clamp(mask.RangeColorAmount, 0, 100) / 100 * 0.16, core = reach * 0.35;
        double lo = mask.RangeLumMin, hi = mask.RangeLumMax, edge = 1 + Math.Clamp(mask.RangeLumSmooth, 0, 100) / 100 * 24;
        bool byLight = lo > 0 || hi < 100;
        float[] px = image.Pixels;

        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                var (lightness, color) = Measure(px[i * 3], px[i * 3 + 1], px[i * 3 + 2], gain, tone);
                double weight = 1;
                if (colors.Length > 0)
                {
                    double best = double.MaxValue;
                    foreach (var c in colors)
                        best = Math.Min(best, Math.Sqrt((color.A - c.A) * (color.A - c.A) + (color.B - c.B) * (color.B - c.B)));
                    weight = 1 - Smooth(core, reach, best);
                }
                if (byLight)
                {
                    if (lo > 0) weight *= Smooth(lo - edge / 2, lo + edge / 2, lightness);
                    if (hi < 100) weight *= 1 - Smooth(hi - edge / 2, hi + edge / 2, lightness);
                }
                result[i] = (float)weight;
            }
        });

        // Трохи розмиваємо (у частках повного кадру — однаково в прев'ю, у 100 % і в експорті).
        double sigma = Math.Max(0.8, 0.002 * frame.FullLongSide * frame.Scale);
        Blur.Gaussian(result, new float[result.Length], w, h, sigma);
        return result;
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / Math.Max(1e-9, b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
