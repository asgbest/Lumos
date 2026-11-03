namespace Lumos.Core;

/// <summary>
/// Відновлення — латка з текстури джерела, підігнана за кольором і яскравістю під оточення; клон — точна копія;
/// ШІ-видалення — фон домальовує нейромережа (див. <see cref="Inpainter"/>).
/// </summary>
public enum HealMode { Heal, Clone, Remove }

/// <summary>
/// Латка відновлювального пензля: мазок (одна точка — кругла пляма) на полотні до обрізки, як і маски,
/// звідки брати латку (зсув у частках полотна), розмір (діаметр у частках довшої сторони), м'якість краю 0…1 і сила 0…1.
/// Для ШІ-видалення — ще й домальована ділянка: PNG (sRGB) у прямокутнику PatchX/Y/W/H (частки полотна);
/// PatchScale — множник, з яким лінійні значення кодувались у картинку (щоб повернути їх назад).
/// </summary>
public sealed record HealSpot(HealMode Mode, double Size, double Feather, double Opacity, EquatableList<MaskPoint> Points,
    double SourceDX, double SourceDY)
{
    public string? Patch { get; init; }
    public double PatchX { get; init; }
    public double PatchY { get; init; }
    public double PatchW { get; init; }
    public double PatchH { get; init; }
    public double PatchScale { get; init; } = 1;
    /// <summary>Сіра PNG у прямокутнику латки: де саме її накладати (тонкий предмет на зразок дроту); null — увесь слід пензля.</summary>
    public string? PatchMask { get; init; }
    /// <summary>
    /// Довгий мазок ШІ-видалення (дріт через увесь кадр) — частини по кількасот пікселів, кожна зі своєю латкою:
    /// одна латка на весь мазок вийшла б розмитою смугою. Для людини це одна латка (Points — увесь мазок).
    /// </summary>
    public EquatableList<HealSpot>? Parts { get; init; }
}

/// <summary>
/// Відновлювальний пензель: прибирає пил, плями, дроти. Латки накладаються по черзі на полотно (повернуте й вирівняне,
/// до обрізки) у лінійному світлі — ще до всіх правок, тож тон і колір латки далі змінюються разом із фото.
/// </summary>
public static class Healing
{
    private const float Eps = 1e-4f;

    /// <summary>Зовнішній радіус латки з м'яким краєм, у пікселях (м'якість 1 — край на половину радіуса ширший).</summary>
    public static double OuterRadius(HealSpot spot, int width, int height) =>
        Math.Max(1, spot.Size * Math.Max(width, height) / 2) * (1 + 0.5 * Math.Clamp(spot.Feather, 0, 1));

    /// <summary>Накладає латки на зображення (змінює його пікселі).</summary>
    public static void Apply(LinearImage canvas, IReadOnlyList<HealSpot> heals)
    {
        foreach (var spot in heals) ApplySpot(canvas, spot);
    }

    private static void ApplySpot(LinearImage img, HealSpot spot)
    {
        if (spot.Points.Count == 0) return;
        if (spot.Parts is { Count: > 0 } parts)
        {
            foreach (var part in parts) ApplySpot(img, part with { Opacity = spot.Opacity });
            return;
        }
        int W = img.Width, H = img.Height;
        // Усередині кола пензля латка непрозора повністю (пляма має зникнути цілком), м'який перехід — назовні.
        double hard = Math.Max(1, spot.Size * Math.Max(W, H) / 2);
        double radius = OuterRadius(spot, W, H);
        var dabs = MaskShapes.Dabs(spot.Points, W, H, Math.Max(1, hard * 0.25));

        // Ділянка: латка + кільце навколо неї шириною в радіус — з кільця беремо колір, під який підганяти латку.
        int margin = (int)Math.Ceiling(radius) + 2;
        int bx0 = Math.Max(0, (int)(dabs.Min(d => d.X) - radius) - margin), by0 = Math.Max(0, (int)(dabs.Min(d => d.Y) - radius) - margin);
        int bx1 = Math.Min(W - 1, (int)(dabs.Max(d => d.X) + radius) + margin), by1 = Math.Min(H - 1, (int)(dabs.Max(d => d.Y) + radius) + margin);
        int bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
        if (bw < 2 || bh < 2) return;

        var weight = Footprint(dabs, radius, hard, bx0, by0, bw, bh);
        double ox = spot.SourceDX * W, oy = spot.SourceDY * H;
        float opacity = (float)Math.Clamp(spot.Opacity, 0, 1);
        float[] px = img.Pixels;

        if (spot.Mode == HealMode.Remove)
        {
            Inpainter.Blend(img, spot, weight, bx0, by0, bw, bh, opacity);
            return;
        }

        // Джерело — те, що було на фото до цієї латки (на випадок, якщо джерело зачіпає саму латку).
        var source = new float[bw * bh * 3];
        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
                for (int c = 0; c < 3; c++)
                    source[(y * bw + x) * 3 + c] = Sample(px, W, H, bx0 + x + 0.5 + ox - 0.5, by0 + y + 0.5 + oy - 0.5, c);
        });

        float[] correction = spot.Mode == HealMode.Heal ? Correction(px, W, source, weight, bx0, by0, bw, bh, radius) : new float[bw * bh * 3];

        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                int i = y * bw + x;
                float a = weight[i] * opacity;
                if (a <= 0) continue;
                int d = ((by0 + y) * W + bx0 + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    float s = source[i * 3 + c];
                    float patch = spot.Mode == HealMode.Heal ? MathF.Max(0, MathF.Exp(MathF.Log(s + Eps) + correction[i * 3 + c]) - Eps) : s;
                    px[d + c] += (patch - px[d + c]) * a;
                }
            }
        });
    }

    /// <summary>
    /// Поправка «латка → оточення» в логарифмах (тобто множник яскравості для кожного каналу): на кільці навколо латки
    /// відома точно (різниця фото й джерела), всередину плавно інтерполюється нормованим розмиттям.
    /// Так латка бере з джерела лише текстуру, а колір і світло — від місця, куди її кладуть.
    /// </summary>
    private static float[] Correction(float[] px, int W, float[] source, float[] weight, int bx0, int by0, int bw, int bh, double radius)
    {
        int n = bw * bh;
        var known = new float[n];
        for (int i = 0; i < n; i++) known[i] = weight[i] <= 0 ? 1 : 0;
        var tmp = new float[n];
        var knownBlur = (float[])known.Clone();
        double sigma = Math.Max(1, radius * 0.5);
        Blur.Gaussian(knownBlur, tmp, bw, bh, sigma);

        var result = new float[n * 3];
        for (int c = 0; c < 3; c++)
        {
            var diff = new float[n];
            double sum = 0;
            int count = 0;
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    int i = y * bw + x;
                    if (known[i] == 0) continue;
                    float dst = px[((by0 + y) * W + bx0 + x) * 3 + c];
                    diff[i] = MathF.Log(dst + Eps) - MathF.Log(source[i * 3 + c] + Eps);
                    sum += diff[i];
                    count++;
                }
            float mean = count > 0 ? (float)(sum / count) : 0;
            Blur.Gaussian(diff, tmp, bw, bh, sigma);
            for (int i = 0; i < n; i++)
                result[i * 3 + c] = knownBlur[i] > 1e-3f ? diff[i] / knownBlur[i] : mean;
        }
        return result;
    }

    /// <summary>Сила латки в кожному пікселі ділянки: відбитки круглого м'якого пензля вздовж мазка (максимум).</summary>
    internal static float[] Footprint(List<(double X, double Y)> dabs, double radius, double hard, int bx0, int by0, int bw, int bh)
    {
        var weight = new float[bw * bh];
        foreach (var (dx, dy) in dabs)
        {
            double cx = dx - bx0, cy = dy - by0;
            int x0 = Math.Max(0, (int)(cx - radius)), x1 = Math.Min(bw - 1, (int)(cx + radius) + 1);
            int y0 = Math.Max(0, (int)(cy - radius)), y1 = Math.Min(bh - 1, (int)(cy + radius) + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                    if (d >= radius) continue;
                    double t = d <= hard ? 0 : (d - hard) / Math.Max(1e-6, radius - hard);
                    float value = (float)(1 - t * t * (3 - 2 * t));
                    int i = y * bw + x;
                    if (value > weight[i]) weight[i] = value;
                }
        }
        return weight;
    }

    /// <summary>
    /// Автовибір джерела, як у Lightroom: перебирає зсуви навколо латки (24 напрямки, кілька відстаней) і бере той,
    /// де кільце навколо джерела найбільше схоже на кільце навколо латки (за візерунком яскравості),
    /// а саме джерело таке ж «рівне», як оточення (щоб не перенести край чи інший предмет). Ближчі — трохи краще.
    /// </summary>
    /// <returns>Зсув джерела в частках полотна.</returns>
    public static (double DX, double DY) FindSource(LinearImage img, HealSpot spot)
    {
        int W = img.Width, H = img.Height;
        double radius = OuterRadius(spot, W, H);
        var dabs = MaskShapes.Dabs(spot.Points, W, H, Math.Max(1, radius * 0.5));
        double minX = dabs.Min(d => d.X), maxX = dabs.Max(d => d.X), minY = dabs.Min(d => d.Y), maxY = dabs.Max(d => d.Y);
        double ring = radius * 1.6, step = Math.Max(1, radius / 6);

        // Точки-проби: всередині латки (inside) і на кільці навколо неї (outside).
        var inside = new List<(double X, double Y)>();
        var outside = new List<(double X, double Y)>();
        for (double y = minY - ring; y <= maxY + ring; y += step)
            for (double x = minX - ring; x <= maxX + ring; x += step)
            {
                double d = Math.Sqrt(dabs.Min(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)));
                if (d < radius) inside.Add((x, y));
                else if (d <= ring) outside.Add((x, y));
            }
        if (inside.Count == 0) inside.Add(dabs[0]);

        float Lum(double x, double y)
        {
            int ix = Math.Clamp((int)x, 0, W - 1), iy = Math.Clamp((int)y, 0, H - 1), i = (iy * W + ix) * 3;
            return MathF.Log(0.2126f * img.Pixels[i] + 0.7152f * img.Pixels[i + 1] + 0.0722f * img.Pixels[i + 2] + 1e-3f);
        }

        var ringDst = outside.Select(p => Lum(p.X, p.Y)).ToArray();
        double ringMean = ringDst.Length > 0 ? ringDst.Average() : Lum(dabs[0].X, dabs[0].Y);
        double ringVar = ringDst.Length > 0 ? ringDst.Average(v => (v - ringMean) * (v - ringMean)) : 0;

        double span = Math.Max(maxX - minX, maxY - minY);
        double best = double.MaxValue, bestX = 2.5 * radius, bestY = 0;
        foreach (double k in new[] { 2.2, 3, 4, 5.5, 7.5, 10 })
        {
            double dist = k * radius + span * 0.5;
            for (int a = 0; a < 24; a++)
            {
                double angle = a * Math.PI / 12, ox = dist * Math.Cos(angle), oy = dist * Math.Sin(angle);
                // Джерело має цілком лежати на фото й не зачіпати саму латку.
                if (minX - radius + ox < 0 || maxX + radius + ox >= W || minY - radius + oy < 0 || maxY + radius + oy >= H) continue;
                bool overlaps = false;
                foreach (var p in inside)
                {
                    double sx = p.X + ox, sy = p.Y + oy;
                    if (dabs.Any(d => (d.X - sx) * (d.X - sx) + (d.Y - sy) * (d.Y - sy) < radius * radius * 1.2)) { overlaps = true; break; }
                }
                if (overlaps) continue;

                double score = 0;
                if (outside.Count > 0)
                {
                    var ringSrc = outside.Select(p => Lum(p.X + ox, p.Y + oy)).ToArray();
                    double srcMean = ringSrc.Average();
                    for (int i = 0; i < ringSrc.Length; i++)
                    {
                        double e = (ringDst[i] - ringMean) - (ringSrc[i] - srcMean);
                        score += e * e;
                    }
                    score /= ringSrc.Length;
                    double shift = srcMean - ringMean;
                    score += (spot.Mode == HealMode.Clone ? 1 : 0.1) * shift * shift;
                }
                var core = inside.Select(p => Lum(p.X + ox, p.Y + oy)).ToArray();
                double coreMean = core.Average(), coreVar = core.Average(v => (v - coreMean) * (v - coreMean));
                score += Math.Abs(coreVar - ringVar) + 0.5 * Math.Max(0, coreVar - ringVar);
                score *= 1 + 0.03 * k; // за інших рівних — ближче
                if (score < best) (best, bestX, bestY) = (score, ox, oy);
            }
        }
        return (bestX / W, bestY / H);
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
}
