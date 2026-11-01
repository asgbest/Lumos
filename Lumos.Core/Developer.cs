namespace Lumos.Core;

/// <summary>
/// Базовий профіль, на який накладаються повзунки.
/// RAW: камера записує дані із запасом на світлі ділянки, тож без підняття яскравості й контрасту фото темне й пласке,
/// а м'яке «плече» не дає пересвіченому обрізатися різко.
/// Готове фото (JPEG тощо): уже оброблене, тому нулі на повзунках мають дати рівно оригінал.
/// </summary>
public sealed record ToneProfile(float BaseExposureEv, double BaseContrast, float ShoulderKnee)
{
    public static readonly ToneProfile Raw = new(0.4f, 12, 0.75f);
    public static readonly ToneProfile Rendered = new(0, 0, 1f);
}

/// <summary>
/// Конвеєр «проявлення»: лінійний RGB → баланс білого → експозиція → насиченість → змішувач HSL
/// → тон і криві (таблиці) → кольорове грейдування → 8-бітний BGRA для екрана.
/// Тонова частина (базовий профіль, контраст, світлі/тіні, білі/чорні, гамма sRGB, криві) зібрана в три таблиці (LUT)
/// — по одній на канал, тому на піксель припадає лише кілька множень і три звертання до таблиць.
/// Змішувач і грейдування рахуються, лише коли їх зрушили з нуля.
/// Це процесорний шлях; той самий конвеєр на відеокарті — <see cref="Gpu.GpuDeveloper"/> (його вибирає <see cref="PhotoRenderer"/>).
/// </summary>
/// <summary>Кеш між кадрами прев'ю: останній результат важких ефектів (чіткість, димка, шум, різкість) — на процесорі чи у відеопам'яті.</summary>
public sealed class RenderCache
{
    internal object? Key;
    internal float[]? Buffer;
    internal object? GpuKey;
    internal object? GpuBuffer;
}

public static class Developer
{
    internal const int LutSize = 16384;
    internal const float LutMax = 16f; // найсвітліше лінійне значення, яке ще розрізняє таблиця (+4 EV над білим)
    private const int CurveTableSize = 4096;

    public const int HistogramBins = 256;

    /// <param name="bgra">Буфер Width × Height × 4 байти.</param>
    /// <param name="histogram">3 × 256 лічильників: R, потім G, потім B (значення після обробки).</param>
    /// <param name="frame">Масштаб зображення відносно оригіналу (для радіусів ефектів) і місце в кадрі.</param>
    /// <param name="cache">Кеш для прев'ю: результат важких ефектів між кадрами (для експорту не потрібен).</param>
    public static void Render(LinearImage src, DevelopSettings s, ToneProfile profile, byte[] bgra, int[] histogram,
        RenderFrame frame, RenderCache? cache = null)
    {
        var (lutR, lutG, lutB) = BuildToneLuts(s, profile);
        HslMixer? mixer = HslMixer.Create(s);
        ColorGrading? grading = ColorGrading.Create(s);
        Vignette? vignette = Vignette.Create(s, frame, src.Width, src.Height);
        Grain? grain = Grain.Create(s, frame);
        var (wr, wg, wb) = WhiteBalanceGains(s);
        float exposure = MathF.Pow(2f, (float)s.Exposure + profile.BaseExposureEv);
        float gr = wr * exposure, gg = wg * exposure, gb = wb * exposure;

        float saturation = 1f + (float)s.Saturation / 100f;
        float vibrance = (float)s.Vibrance / 100f;
        const float lutScale = 1f / LutMax;
        const int lutLast = LutSize - 1;

        int w = src.Width;
        float[] px = src.Pixels;
        if (SpatialEffects.Active(s))
        {
            px = SpatialStage(src, s, frame, wr, wg, wb, cache);
            gr = gg = gb = exposure;
        }
        Array.Clear(histogram);

        Parallel.For(0, src.Height, () => new int[HistogramBins * 3], (y, _, local) =>
        {
            int i = y * w * 3, o = y * w * 4;
            for (int x = 0; x < w; x++, i += 3, o += 4)
            {
                float r = px[i] * gr, g = px[i + 1] * gg, b = px[i + 2] * gb;
                if (vignette is not null)
                {
                    float f = vignette.Factor(x, y);
                    r *= f; g *= f; b *= f;
                }

                // Змішувач кольорів — до загальної насиченості: так при насиченості −100 яскравість кольорів у змішувачі
                // керує чорно-білим перетворенням (зелень темніше, листя світліше), як панель Ч/Б у Lightroom.
                mixer?.Apply(ref r, ref g, ref b);

                // Насиченість і яскравість кольорів: відсуваємо канали від сірого тієї ж яскравості.
                // Яскравість кольорів сильніше підсилює ненасичені кольори й майже не чіпає вже яскраві.
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                float max = MathF.Max(r, MathF.Max(g, b));
                float min = MathF.Min(r, MathF.Min(g, b));
                float chroma = max > 1e-6f ? (max - min) / max : 0f;
                float k = MathF.Max(0f, saturation + vibrance * (1f - chroma));
                r = MathF.Max(0f, lum + (r - lum) * k);
                g = MathF.Max(0f, lum + (g - lum) * k);
                b = MathF.Max(0f, lum + (b - lum) * k);

                // Таблиця з кроком за коренем — щільніша в тінях, де око помічає найменші переходи.
                float vr = lutR[Math.Min(lutLast, (int)(MathF.Sqrt(r * lutScale) * lutLast + 0.5f))];
                float vg = lutG[Math.Min(lutLast, (int)(MathF.Sqrt(g * lutScale) * lutLast + 0.5f))];
                float vb = lutB[Math.Min(lutLast, (int)(MathF.Sqrt(b * lutScale) * lutLast + 0.5f))];

                grading?.Apply(ref vr, ref vg, ref vb);
                if (grain is not null)
                {
                    // Зерно однакове для всіх каналів — як на плівці, без кольорових плям.
                    float before = 0.2126f * vr + 0.7152f * vg + 0.0722f * vb;
                    float shift = grain.Apply(before, x, y) - before;
                    vr += shift; vg += shift; vb += shift;
                }

                byte br = ToByte(vr), bg = ToByte(vg), bb = ToByte(vb);

                bgra[o] = bb;
                bgra[o + 1] = bg;
                bgra[o + 2] = br;
                bgra[o + 3] = 255;
                local[br]++;
                local[HistogramBins + bg]++;
                local[2 * HistogramBins + bb]++;
            }
            return local;
        }, local =>
        {
            lock (histogram)
                for (int j = 0; j < local.Length; j++) histogram[j] += local[j];
        });
    }

    /// <summary>
    /// Ефекти з сусідніми пікселями — на зображенні з балансом білого, але ще без експозиції.
    /// Результат не залежить від експозиції, тону й кольору, тож поки тягнуть ці повзунки, він береться з кешу
    /// і кадр рахується так само швидко, як без ефектів.
    /// </summary>
    private static float[] SpatialStage(LinearImage src, DevelopSettings s, RenderFrame frame, float wr, float wg, float wb, RenderCache? cache)
    {
        var key = new SpatialKey(src, wr, wg, wb, s.Texture, s.Clarity, s.Dehaze, s.SharpenAmount, s.SharpenRadius,
            s.SharpenDetail, s.SharpenMasking, s.NoiseReduction, s.ColorNoiseReduction, frame.Scale, frame.FullLongSide);
        if (cache is { Key: SpatialKey cached, Buffer: { } ready } && cached == key) return ready;

        int count = src.Pixels.Length, w = src.Width;
        var dst = cache?.Buffer is { } reuse && reuse.Length == count ? reuse : new float[count];
        float[] px = src.Pixels;
        Parallel.For(0, src.Height, y =>
        {
            for (int i = y * w * 3, end = i + w * 3; i < end; i += 3)
            {
                dst[i] = px[i] * wr;
                dst[i + 1] = px[i + 1] * wg;
                dst[i + 2] = px[i + 2] * wb;
            }
        });
        SpatialEffects.Apply(dst, w, src.Height, s, frame);
        if (cache is not null)
        {
            cache.Key = key;
            cache.Buffer = dst;
        }
        return dst;
    }

    private readonly record struct SpatialKey(LinearImage Source, float R, float G, float B,
        double Texture, double Clarity, double Dehaze, double SharpenAmount, double SharpenRadius, double SharpenDetail,
        double SharpenMasking, double NoiseReduction, double ColorNoiseReduction, double Scale, int FullLongSide);

    /// <summary>Множники каналів: температура — синій ↔ жовтий, відтінок — зелений ↔ пурпуровий. Загальна яскравість зберігається.</summary>
    internal static (float R, float G, float B) WhiteBalanceGains(DevelopSettings s)
    {
        float t = (float)s.Temperature / 100f, tint = (float)s.Tint / 100f;
        float r = MathF.Pow(2f, 0.6f * t), b = MathF.Pow(2f, -0.6f * t), g = MathF.Pow(2f, -0.4f * tint);
        float norm = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        return (r / norm, g / norm, b / norm);
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    /// <summary>Три таблиці (R, G, B): спільний тон і загальна крива, а потім крива свого каналу.</summary>
    internal static (float[] R, float[] G, float[] B) BuildToneLuts(DevelopSettings s, ToneProfile profile)
    {
        float[] tone = BuildToneLut(s, profile);
        float[]? master = TableOrNull(s.CurveMaster);
        if (master is not null)
            for (int i = 0; i < tone.Length; i++) tone[i] = Lookup(master, tone[i]);

        return (Channel(s.CurveRed), Channel(s.CurveGreen), Channel(s.CurveBlue));

        float[] Channel(Curve curve)
        {
            if (TableOrNull(curve) is not { } table) return tone;
            var lut = new float[tone.Length];
            for (int i = 0; i < lut.Length; i++) lut[i] = Lookup(table, tone[i]);
            return lut;
        }
    }

    private static float[]? TableOrNull(Curve curve) => curve.IsIdentity ? null : curve.Table(CurveTableSize);

    private static float Lookup(float[] table, float v)
    {
        float x = Math.Clamp(v, 0f, 1f) * (table.Length - 1);
        int i = Math.Min((int)x, table.Length - 2);
        return table[i] + (table[i + 1] - table[i]) * (x - i);
    }

    private static float[] BuildToneLut(DevelopSettings s, ToneProfile profile)
    {
        float contrast = (float)((s.Contrast + profile.BaseContrast) / 100);
        float highlights = (float)s.Highlights / 100f, shadows = (float)s.Shadows / 100f;
        float whites = (float)s.Whites / 100f, blacks = (float)s.Blacks / 100f;

        // Готове фото без запасу над білим: коли експозицію підняли, вмикаємо м'яке «плече», щоб світле не випалювалось
        // різко в суцільне біле. На нулі коліно = 1, і оригінал лишається точно таким, як був.
        float knee = profile.ShoulderKnee;
        if (knee >= 1 && s.Exposure > 0)
            knee = 1 - 0.25f * MathF.Min(1, (float)s.Exposure);

        var lut = new float[LutSize];
        float prev = 0f;
        for (int i = 0; i < LutSize; i++)
        {
            float u = i / (float)(LutSize - 1);
            float linear = u * u * LutMax;

            // Далі працюємо в «перцептивних» значеннях 0…1 (як бачить око): 0 — чорний, 1 — білий.
            float v = EncodeSrgb(Shoulder(linear, knee));
            v = ApplyContrast(v, contrast);
            v += 0.22f * shadows * 6.75f * v * (1 - v) * (1 - v);   // тіні: найсильніше біля 1/3
            v += 0.22f * highlights * 6.75f * v * v * (1 - v);      // світлі: найсильніше біля 2/3
            v *= 1 + 0.25f * whites * v * v;                         // білі: зсуває точку білого
            v += 0.12f * blacks * (1 - v) * (1 - v) * (1 - v);       // чорні: зсуває точку чорного

            // Сильні повзунки не повинні ламати порядок яскравостей (світліше має лишатися світлішим).
            v = Math.Clamp(v, 0f, 1f);
            prev = MathF.Max(prev, v);
            lut[i] = prev;
        }
        return lut;
    }

    /// <summary>М'яке «плече»: до <paramref name="knee"/> нічого не змінює, вище плавно стискає, тож пересвічене не обрізається різко.
    /// Коліно 1 — без стискання: світліше за біле просто обрізається (для готових фото, де біле вже на своєму місці).</summary>
    private static float Shoulder(float x, float knee)
    {
        if (knee >= 1) return MathF.Min(x, 1);
        if (x <= knee) return x;
        return knee + (1 - knee) * (1 - MathF.Exp(-(x - knee) / (1 - knee)));
    }

    /// <summary>S-крива навколо середньо-сірого: кінці 0 і 1 лишаються на місці.</summary>
    private static float ApplyContrast(float v, float amount)
    {
        const float pivot = 0.46f;
        float k = MathF.Pow(2f, amount * 1.2f);
        return v < pivot
            ? pivot * MathF.Pow(v / pivot, k)
            : 1 - (1 - pivot) * MathF.Pow((1 - v) / (1 - pivot), k);
    }

    private static float EncodeSrgb(float x) =>
        x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;
}
