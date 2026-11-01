using System.Globalization;

namespace Lumos.Core;

/// <summary>
/// Вимірювання рендера для перевірки Gemini: яскравість (L* 0…100) по третинах кадру, «провали» в тінях і світлах,
/// відтінок нейтральних (сірих) ділянок і середня насиченість. На око модель часто не помічає брудних тіней
/// чи пурпурного відтінку асфальту й гір — числа помічають, а <see cref="Problems"/> називає це прямо.
/// </summary>
public sealed record LookMetrics(double Top, double Middle, double Bottom, double Mean, double ShadowClip, double HighlightClip,
    double CastA, double CastB, double Chroma)
{
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        double c = i / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }).ToArray();

    public static LookMetrics Measure(byte[] bgra, int width, int height)
    {
        // ~300 тис. точок досить: це оцінка, а не гістограма для друку.
        int step = Math.Max(1, (int)Math.Sqrt(width * (double)height / 300_000));
        double[] zoneSum = new double[3];
        long[] zoneCount = new long[3];
        long count = 0, dark = 0, bright = 0, neutral = 0;
        double sumA = 0, sumB = 0, sumChroma = 0;
        for (int y = 0; y < height; y += step)
        {
            int zone = Math.Min(2, y * 3 / height);
            for (int x = 0; x < width; x += step)
            {
                int o = (y * width + x) * 4;
                var (l, a, b) = Lab(Linear[bgra[o + 2]], Linear[bgra[o + 1]], Linear[bgra[o]]);
                zoneSum[zone] += l;
                zoneCount[zone]++;
                count++;
                if (l < 3) dark++;
                if (l > 98) bright++;
                double chroma = Math.Sqrt(a * a + b * b);
                sumChroma += chroma;
                // Майже сірі середні тони (асфальт, хмари, бетон) — по них видно загальний відтінок.
                if (chroma < 12 && l is > 15 and < 90)
                {
                    sumA += a;
                    sumB += b;
                    neutral++;
                }
            }
        }
        double Zone(int i) => zoneCount[i] > 0 ? zoneSum[i] / zoneCount[i] : 0;
        double total = Math.Max(1, count);
        return new LookMetrics(Zone(0), Zone(1), Zone(2), (zoneSum[0] + zoneSum[1] + zoneSum[2]) / total,
            100.0 * dark / total, 100.0 * bright / total,
            neutral > 0 ? sumA / neutral : 0, neutral > 0 ? sumB / neutral : 0, sumChroma / total);
    }

    /// <summary>Числа для моделі (англійською, як і весь запит).</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"brightness L* top/middle/bottom third {Top:0}/{Middle:0}/{Bottom:0} (mean {Mean:0}); crushed blacks {ShadowClip:0.0}% · blown highlights {HighlightClip:0.0}%; " +
        $"neutral-gray cast a* {CastA:+0.0;-0.0;0} (+magenta/−green), b* {CastB:+0.0;-0.0;0} (+yellow/−blue); mean chroma {Chroma:0}");

    /// <summary>Явні огріхи рендера відносно оригіналу; порожньо — нічого підозрілого.</summary>
    public List<string> Problems(LookMetrics original)
    {
        var problems = new List<string>();
        // Межі абсолютні: оригінал буває недоекспонованим, і «не темніше за нього» ще не означає «добре».
        // Денний кадр зазвичай має середнє L* 45–60, а землю й передній план — не темніше ~35.
        const string unlessNight = "unless this is a night or deliberately low-key scene";
        void Zone(string name, double value)
        {
            if (value < 35)
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"the {name} third is dark and muddy (L* {value:0}) — lift it with exposure/shadows or a mask, {unlessNight}"));
        }
        Zone("top", Top);
        Zone("middle", Middle);
        Zone("bottom", Bottom);
        if (Mean < 40)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"the image is dark overall (mean L* {Mean:0}; a daylight scene usually sits at 45–60), {unlessNight}"));
        if (ShadowClip > 3 && ShadowClip > original.ShadowClip + 2)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"crushed blacks: {ShadowClip:0.0}% of the image is pure black"));
        if (HighlightClip > 2 && HighlightClip > original.HighlightClip + 1)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"blown highlights: {HighlightClip:0.0}% of the image is pure white"));

        double da = CastA - original.CastA, db = CastB - original.CastB;
        if (CastA > 2.5 && da > 1.5 && CastB < 0.5) // пурпур + синява = фіолетовий
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"purple/magenta cast on neutral grays (a* {CastA:+0.0;-0.0;0}, b* {CastB:+0.0;-0.0;0}): roads, clouds and mountains look violet — usually from Tint, magenta/blue grading or curves; remove it"));
        else if (Math.Abs(da) > 5 || Math.Abs(db) > 7)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"strong color cast on neutral grays (a* {CastA:+0.0;-0.0;0}, b* {CastB:+0.0;-0.0;0} vs original {original.CastA:+0.0;-0.0;0}, {original.CastB:+0.0;-0.0;0}) — keep it only if this tint is the clear intent of the look"));
        if (Chroma > 30 && Chroma > original.Chroma * 1.7)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"oversaturated (mean chroma {Chroma:0} vs {original.Chroma:0})"));
        return problems;
    }

    private static (double L, double A, double B) Lab(double r, double g, double b)
    {
        // Лінійний sRGB → XYZ (D65) → CIELAB.
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
