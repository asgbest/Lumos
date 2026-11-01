namespace Lumos.Core;

/// <summary>
/// Змішувач кольорів (HSL): для кожного з 8 кольорів — зсув відтінку, насиченість, яскравість.
/// Налаштування розгортаються в таблицю на кожен градус відтінку з плавними переходами між сусідніми кольорами,
/// тож помаранчевий небосхил чи шкіра змінюються без різких меж. Сірі пікселі не чіпаються.
/// </summary>
internal sealed class HslMixer
{
    // Центри кольорів на колі відтінків (градуси): червоний, помаранчевий, жовтий, зелений, бірюзовий, синій, фіолетовий, пурпуровий.
    private static readonly float[] Centers = [0, 30, 60, 120, 180, 240, 270, 300];

    internal readonly float[] _shift = new float[360], _saturation = new float[360], _luminanceEv = new float[360];

    /// <summary>null — якщо змішувач на нулях (тоді обробка пікселів його пропускає).</summary>
    public static HslMixer? Create(DevelopSettings s)
    {
        var bands = Enum.GetValues<HslBand>().Select(s.GetHsl).ToArray();
        return bands.All(b => b == default) ? null : new HslMixer(bands);
    }

    private HslMixer(HslAdjust[] bands)
    {
        for (int h = 0; h < 360; h++)
        {
            int i = Centers.Length - 1;
            while (i > 0 && h < Centers[i]) i--;
            int j = (i + 1) % Centers.Length;
            float span = (j == 0 ? 360 : Centers[j]) - Centers[i];
            float t = (h - Centers[i]) / span;
            t = t * t * (3 - 2 * t);

            HslAdjust a = bands[i], b = bands[j];
            _shift[h] = Lerp(a.Hue, b.Hue, t) / 100f * 30f;             // ±30° — до сусіднього кольору
            _saturation[h] = 1 + Lerp(a.Saturation, b.Saturation, t) / 100f;
            _luminanceEv[h] = Lerp(a.Luminance, b.Luminance, t) / 100f;  // ±1 EV
        }
    }

    public void Apply(ref float r, ref float g, ref float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;
        if (max <= 1e-6f || delta <= 1e-6f * max) return;

        float sat = delta / max;
        float hue = max == r ? 60 * ((g - b) / delta) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        int idx = (int)hue;
        if (idx >= 360) idx = 0;

        // Майже сірі кольори змінюємо слабше, інакше з'являються кольорові плями на нейтральному.
        float weight = MathF.Min(1, sat * 4);
        float newHue = hue + _shift[idx] * weight;
        if (newHue < 0) newHue += 360; else if (newHue >= 360) newHue -= 360;
        float newSat = MathF.Min(1, sat * _saturation[idx]);
        float value = max * MathF.Pow(2f, _luminanceEv[idx] * weight);

        HsvToRgb(newHue, newSat, value, out r, out g, out b);
    }

    internal static void HsvToRgb(float hue, float sat, float value, out float r, out float g, out float b)
    {
        float c = value * sat, hp = hue / 60f;
        float x = c * (1 - MathF.Abs(hp % 2 - 1)), m = value - c;
        (r, g, b) = (int)hp switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        r += m; g += m; b += m;
    }

    private static float Lerp(double a, double b, float t) => (float)(a + (b - a) * t);
}

/// <summary>
/// Кольорове грейдування: тіні, середні тони й світлі отримують свій відтінок і яскравість (плюс загальне колесо).
/// Працює з уже готовими (екранними) значеннями 0…1, як фінальний «кольоровий фільтр» поверх решти правок.
/// «Змішування» — наскільки зони перекриваються; «Баланс» — зсуває межу між тінями й світлими.
/// </summary>
internal sealed class ColorGrading
{
    internal readonly float[] _shadows, _midtones, _highlights, _global; // зсув R, G, B (уже з яскравістю)
    internal readonly float _exponent, _balance;

    public static ColorGrading? Create(DevelopSettings s)
    {
        GradeWheel[] wheels = [s.GradeShadows, s.GradeMidtones, s.GradeHighlights, s.GradeGlobal];
        return wheels.All(w => w.Saturation == 0 && w.Luminance == 0) ? null : new ColorGrading(s);
    }

    private ColorGrading(DevelopSettings s)
    {
        _shadows = Offset(s.GradeShadows);
        _midtones = Offset(s.GradeMidtones);
        _highlights = Offset(s.GradeHighlights);
        _global = Offset(s.GradeGlobal);
        _exponent = (float)(3 - 2 * s.GradeBlending / 100); // 0 → чіткі зони, 100 → сильне перекриття
        _balance = MathF.Pow(2f, (float)(-s.GradeBalance / 100));
    }

    /// <summary>Відтінок колеса без зміни яскравості (чистий «колір») × сила, плюс зсув яскравості.</summary>
    private static float[] Offset(GradeWheel w)
    {
        HslMixer.HsvToRgb((float)(w.Hue % 360 + 360) % 360, 1, 1, out float r, out float g, out float b);
        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        float strength = (float)w.Saturation / 100f * 0.3f, lum = (float)w.Luminance / 100f * 0.3f;
        return [(r - luma) * strength + lum, (g - luma) * strength + lum, (b - luma) * strength + lum];
    }

    public void Apply(ref float r, ref float g, ref float b)
    {
        float luma = Math.Clamp(0.2126f * r + 0.7152f * g + 0.0722f * b, 0, 1);
        float l = MathF.Pow(luma, _balance);
        float ws = MathF.Pow(1 - l, _exponent), wh = MathF.Pow(l, _exponent);
        float wm = MathF.Max(0, 1 - ws - wh);

        r += ws * _shadows[0] + wm * _midtones[0] + wh * _highlights[0] + _global[0];
        g += ws * _shadows[1] + wm * _midtones[1] + wh * _highlights[1] + _global[1];
        b += ws * _shadows[2] + wm * _midtones[2] + wh * _highlights[2] + _global[2];
    }
}
