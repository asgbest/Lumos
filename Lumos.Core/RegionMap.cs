using System.Globalization;
using System.Text;
using ImageMagick;
using ImageMagick.Drawing;

namespace Lumos.Core;

/// <summary>Вимірювання однієї ділянки кадру: частка кадру (%), яскравість L* (середня, 10-й і 90-й перцентилі),
/// деталь (середня різниця L* сусідніх точок — хмари, фактура), насиченість і відтінок (градуси Lab), частка «вибитого» й «провального».</summary>
public sealed record RegionStats(double Coverage, double Mean, double Low, double High, double Detail, double Chroma, double Hue,
    double Blown, double Crushed);

/// <summary>Ділянка кадру, яку знайшла нейромережа Lumos: назва для Gemini (як вид маски), маска й рамка (частки кадру).</summary>
public sealed record Region(string Key, string Name, MaskKind Kind, float[] Mask, double CenterX, double CenterY,
    double Left, double Top, double Right, double Bottom);

/// <summary>Вимірювання рендера по ділянках; Subject* — наскільки головний об'єкт відрізняється від свого оточення.</summary>
public sealed record RegionReport(Dictionary<string, RegionStats> Stats, double? SubjectDeltaL, double? SubjectDeltaChroma);

/// <summary>
/// «✨ AI Pro»: карта ділянок кадру. Lumos сам проганяє свої нейромережі (небо, головний об'єкт, вода, рослинність, гори…)
/// і дає Gemini не здогадки по картинці, а факти: де яка ділянка, яка вона яскрава, детальна й кольорова, скільки в небі
/// вибито і скільки хмар насправді є в RAW (рендер на −1,5 EV). Ті самі маски міряють кожен варіант — чи небо повернулось,
/// чи об'єкт виділився, чи ділянка не «провалилась».
/// </summary>
public sealed class RegionMap
{
    public const double RecoveryEv = -1.5;
    private const int GridSide = 640; // вимірювання — на сітці ~640 точок по довшій стороні

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<Region> Regions { get; }
    /// <summary>Кадр «як є» з підписаними кольоровими ділянками (JPEG) — щоб Gemini бачив, що як називається.</summary>
    public byte[] Overlay { get; }
    /// <summary>Кадр «як є» на −1,5 EV (JPEG): що ховається у світлах RAW.</summary>
    public byte[] Recovery { get; }
    public RegionReport Original { get; }
    public RegionReport Recovered { get; }
    public bool IsRaw { get; }

    private readonly Dictionary<(int, int), Dictionary<string, bool[]>> _gridMasks = [];
    private readonly Dictionary<(int, int), bool[]?> _subjectRings = [];

    private RegionMap(int width, int height, List<Region> regions, byte[] asShot, byte[] recovery, bool isRaw)
    {
        (Width, Height, Regions, IsRaw) = (width, height, regions, isRaw);
        Original = Measure(asShot, width, height);
        Recovered = Measure(recovery, width, height);
        Overlay = DrawOverlay(asShot, width, height, regions);
        Recovery = AiLooks.ToJpeg(recovery, width, height, AiStylist.ImageSide);
    }

    /// <param name="types">ШІ-маски, чиї моделі встановлені (без завантажень посеред запиту).</param>
    public static RegionMap Build(string path, Photo photo, LinearImage preview, DevelopSettings settings, ToneProfile profile,
        IReadOnlyList<AiMaskType> types, CancellationToken cancel = default)
    {
        var (bgra, w, h) = AiLooks.RenderAsShot(path, preview, settings, profile, withHeals: true);
        var regions = new List<Region>();
        foreach (var type in types)
        {
            cancel.ThrowIfCancellationRequested();
            AiSegmenter.Result r;
            try { r = AiLooks.SegmentAiMask(type, path, preview, settings, profile).Result; }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException) { continue; }
            if (r.Width != w || r.Height != h) continue;
            if (ToRegion(type, r.Mask, w, h) is { } region) regions.Add(region);
        }
        var (dark, _, _) = AiLooks.RenderAsShot(path, preview, settings, profile, withHeals: true, exposure: RecoveryEv);
        return new RegionMap(w, h, regions, bgra, dark, photo.Kind == PhotoKind.Raw);
    }

    private static Region? ToRegion(AiMaskType type, float[] mask, int w, int h)
    {
        long count = 0;
        double sx = 0, sy = 0;
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                if (mask[y * w + x] < 0.5f) continue;
                count++;
                sx += x;
                sy += y;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        double coverage = 100.0 * count / ((w / 2.0) * (h / 2.0));
        // Об'єкт — від крихітного (човен удалині) до половини кадру; ландшафт і небо — помітні ділянки, не шум.
        bool keep = type.Kind == MaskKind.Subject ? coverage is > 0.1 and < 55 : coverage > 1.5;
        if (!keep || right < 0) return null;

        // Підпис — у точці маски, найближчій до центру мас (у «підкови» центр мас може бути поза нею).
        int cx = (int)(sx / count), cy = (int)(sy / count);
        if (mask[cy * w + cx] < 0.5f)
        {
            double best = double.MaxValue;
            int bx = cx, by = cy;
            for (int y = 0; y < h; y += 4)
                for (int x = 0; x < w; x += 4)
                {
                    if (mask[y * w + x] < 0.5f) continue;
                    double d = (x - cx) * (double)(x - cx) + (y - cy) * (double)(y - cy);
                    if (d < best) (best, bx, by) = (d, x, y);
                }
            (cx, cy) = (bx, by);
        }
        string key = type.Kind == MaskKind.Subject ? "subject" : AiStylist.KindName(type.Kind);
        return new Region(key, type.Name, type.Kind, mask, (cx + 0.5) / w, (cy + 0.5) / h,
            left / (double)w, top / (double)h, (right + 2) / (double)w, (bottom + 2) / (double)h);
    }

    // ── Вимірювання ───────────────────────────────────────────────────────────

    /// <summary>Ділянки на рендері будь-якого розміру з тим самим кадром (без обрізки).</summary>
    public RegionReport Measure(byte[] bgra, int width, int height)
    {
        int step = Math.Max(1, Math.Max(width, height) / GridSide);
        int gw = (width + step - 1) / step, gh = (height + step - 1) / step;
        var L = new double[gw * gh];
        var A = new double[gw * gh];
        var B = new double[gw * gh];
        Parallel.For(0, gh, gy =>
        {
            for (int gx = 0; gx < gw; gx++)
            {
                int o = (Math.Min(height - 1, gy * step) * width + Math.Min(width - 1, gx * step)) * 4;
                var (l, a, b) = LookMetrics.Lab(LookMetrics.Linear[bgra[o + 2]], LookMetrics.Linear[bgra[o + 1]], LookMetrics.Linear[bgra[o]]);
                (L[gy * gw + gx], A[gy * gw + gx], B[gy * gw + gx]) = (l, a, b);
            }
        });

        var masks = GridMasks(gw, gh, step, width, height);
        var stats = new Dictionary<string, RegionStats>();
        foreach (var region in Regions)
            if (Stats(masks[region.Key], L, A, B, gw, gh) is { } s) stats[region.Key] = s;

        double? deltaL = null, deltaChroma = null;
        if (stats.TryGetValue("subject", out var subject) && Ring(gw, gh, masks["subject"]) is { } ring && Stats(ring, L, A, B, gw, gh) is { } around)
            (deltaL, deltaChroma) = (subject.Mean - around.Mean, subject.Chroma - around.Chroma);
        return new RegionReport(stats, deltaL, deltaChroma);
    }

    private Dictionary<string, bool[]> GridMasks(int gw, int gh, int step, int width, int height)
    {
        if (_gridMasks.TryGetValue((gw, gh), out var cached)) return cached;
        var masks = new Dictionary<string, bool[]>();
        foreach (var region in Regions)
        {
            var grid = new bool[gw * gh];
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    int mx = Math.Min(Width - 1, (int)((Math.Min(width - 1, gx * step) + 0.5) * Width / width));
                    int my = Math.Min(Height - 1, (int)((Math.Min(height - 1, gy * step) + 0.5) * Height / height));
                    grid[gy * gw + gx] = region.Mask[my * Width + mx] >= 0.5f;
                }
            masks[region.Key] = grid;
        }
        return _gridMasks[(gw, gh)] = masks;
    }

    /// <summary>Оточення об'єкта: усе поза ним у рамці, удвічі більшій за його власну.</summary>
    private bool[]? Ring(int gw, int gh, bool[] subject)
    {
        if (_subjectRings.TryGetValue((gw, gh), out var cached)) return cached;
        var r = Regions.First(x => x.Key == "subject");
        double hw = (r.Right - r.Left), hh = (r.Bottom - r.Top);
        int x0 = (int)(Math.Max(0, r.Left - hw / 2) * gw), x1 = (int)(Math.Min(1, r.Right + hw / 2) * gw);
        int y0 = (int)(Math.Max(0, r.Top - hh / 2) * gh), y1 = (int)(Math.Min(1, r.Bottom + hh / 2) * gh);
        var ring = new bool[gw * gh];
        int count = 0;
        for (int y = y0; y < Math.Min(gh, y1); y++)
            for (int x = x0; x < Math.Min(gw, x1); x++)
                if (!subject[y * gw + x]) { ring[y * gw + x] = true; count++; }
        return _subjectRings[(gw, gh)] = count > 20 ? ring : null;
    }

    private static RegionStats? Stats(bool[] inside, double[] L, double[] A, double[] B, int gw, int gh)
    {
        var values = new List<double>();
        double sumA = 0, sumB = 0, sumChroma = 0, detail = 0;
        long pairs = 0, blown = 0, crushed = 0;
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                int i = y * gw + x;
                if (!inside[i]) continue;
                double l = L[i];
                values.Add(l);
                sumA += A[i];
                sumB += B[i];
                sumChroma += Math.Sqrt(A[i] * A[i] + B[i] * B[i]);
                if (l > 98) blown++;
                if (l < 3) crushed++;
                if (x + 1 < gw && inside[i + 1]) { detail += Math.Abs(l - L[i + 1]); pairs++; }
                if (y + 1 < gh && inside[i + gw]) { detail += Math.Abs(l - L[i + gw]); pairs++; }
            }
        if (values.Count < 12) return null;
        values.Sort();
        int n = values.Count;
        double hue = (Math.Atan2(sumB / n, sumA / n) * 180 / Math.PI + 360) % 360;
        return new RegionStats(100.0 * n / (gw * gh), values.Average(), values[n / 10], values[n * 9 / 10],
            pairs > 0 ? detail / pairs : 0, sumChroma / n, hue, 100.0 * blown / n, 100.0 * crushed / n);
    }

    // ── Для Gemini ────────────────────────────────────────────────────────────

    /// <summary>Чи є в RAW деталь у вибитому небі: на −1,5 EV вибитого помітно менше, а фактура — є.</summary>
    public bool SkyRecoverable =>
        Original.Stats.TryGetValue("sky", out var sky) && Recovered.Stats.TryGetValue("sky", out var dark)
        && sky.Blown > 3 && dark.Blown < sky.Blown * 0.4;

    /// <summary>Опис ділянок кадру «як є» (англійською, як і весь запит).</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (Regions.Count == 0) return "Lumos's segmentation found no distinct regions (no sky, landscape classes or clear subject).";
        sb.AppendLine("REGIONS found by Lumos's own segmentation networks (exact pixel masks; use these keys as mask kinds; all values measured on the as-shot render, CIELAB L* 0…100):");
        foreach (var r in Regions)
        {
            if (!Original.Stats.TryGetValue(r.Key, out var s)) continue;
            sb.Append(Inv($"- {r.Key}: {s.Coverage:0.#}% of the frame, box x {r.Left:0.00}–{r.Right:0.00}, y {r.Top:0.00}–{r.Bottom:0.00}, label at ({r.CenterX:0.00}, {r.CenterY:0.00}); "));
            sb.Append(Line(s));
            if (r.Key == "subject" && Original.SubjectDeltaL is { } dl)
                sb.Append(Inv($"; vs its surroundings: ΔL* {dl:+0;-0;0}, Δchroma {Original.SubjectDeltaChroma:+0;-0;0}"));
            if (Recovered.Stats.TryGetValue(r.Key, out var d) && s.Blown > 3)
                sb.Append(Inv($"; at {RecoveryEv:0.0} EV: {d.Blown:0.#}% blown, detail {d.Detail:0.0}"));
            sb.AppendLine();
        }
        if (SkyRecoverable)
            sb.AppendLine(IsRaw
                ? "SKY HEADROOM: the sky looks white as shot, but the RAW holds real cloud texture (see the −1.5 EV preview). Bringing it back is usually the single biggest improvement: on a sky mask lower Exposure/Highlights/Whites strongly, then shape it with Contrast/Clarity/Dehaze."
                : "The sky is partly blown; this is a rendered file, so only part of it can be recovered.");
        return sb.ToString();
    }

    /// <summary>Ділянки одного варіанта поруч із оригіналом — щоб Gemini бачив, що змінилось де.</summary>
    public string Describe(RegionReport look)
    {
        var parts = new List<string>();
        foreach (var r in Regions)
            if (look.Stats.TryGetValue(r.Key, out var s) && Original.Stats.TryGetValue(r.Key, out var o))
                parts.Add(Inv($"{r.Key} L* {o.Mean:0}→{s.Mean:0}, detail {o.Detail:0.0}→{s.Detail:0.0}, chroma {o.Chroma:0}→{s.Chroma:0}, blown {o.Blown:0.#}%→{s.Blown:0.#}%"));
        if (look.SubjectDeltaL is { } dl && Original.SubjectDeltaL is { } odl)
            parts.Add(Inv($"subject vs surroundings ΔL* {odl:+0;-0;0}→{dl:+0;-0;0}, Δchroma {Original.SubjectDeltaChroma:+0;-0;0}→{look.SubjectDeltaChroma:+0;-0;0}"));
        return parts.Count == 0 ? "" : "Regions (as shot → this look): " + string.Join("; ", parts);
    }

    /// <summary>Огріхи по ділянках: небо лишилось вибитим, хоч у RAW є хмари; об'єкт не виділився; ділянка «провалилась».</summary>
    public List<string> Problems(RegionReport look)
    {
        var problems = new List<string>();
        if (SkyRecoverable && look.Stats.TryGetValue("sky", out var sky) && Recovered.Stats.TryGetValue("sky", out var dark))
        {
            if (sky.Blown > Math.Max(3, dark.Blown * 2))
                problems.Add(Inv($"the sky is still blown ({sky.Blown:0}% pure white) although the RAW holds cloud detail — on the sky mask pull Exposure/Highlights/Whites down much further"));
            else if (sky.Detail < dark.Detail * 0.6)
                problems.Add(Inv($"the clouds are still weak (sky detail {sky.Detail:0.0}; the RAW allows ~{dark.Detail:0.0}) — more sky Highlights −, Contrast/Clarity/Dehaze +"));
        }
        else if (look.Stats.TryGetValue("sky", out var s) && Original.Stats.TryGetValue("sky", out var o)
                 && s.Mean > 68 && s.Mean > o.Mean + 12 && s.Detail < o.Detail * 0.85)
        {
            // Загальне висвітлення «вибілює» хмари: небо світлішає, а фактура зникає.
            problems.Add(Inv($"the sky got bright and flat (L* {o.Mean:0}→{s.Mean:0}, cloud detail {o.Detail:0.0}→{s.Detail:0.0}) — the clouds lose their volume; lift the dark areas with Shadows and their own region masks instead of global Exposure, and on the sky mask lower Exposure/Highlights/Whites and add Contrast/Dehaze so the clouds keep their shape, unless a soft high-key look is the intent"));
        }
        if (look.SubjectDeltaL is { } dl && Original.SubjectDeltaL is { } odl)
        {
            double pop = Math.Abs(dl) + Math.Max(0, look.SubjectDeltaChroma ?? 0);
            double was = Math.Abs(odl) + Math.Max(0, Original.SubjectDeltaChroma ?? 0);
            if (pop < was)
                problems.Add(Inv($"the subject stands out less than as shot (|ΔL*| {Math.Abs(dl):0}, Δchroma {look.SubjectDeltaChroma:+0;-0;0}) — lift or enrich it on the subject mask and/or calm its surroundings"));
        }
        foreach (var r in Regions)
            if (r.Key != "sky" && look.Stats.TryGetValue(r.Key, out var s) && Original.Stats.TryGetValue(r.Key, out var o)
                && s.Mean < 22 && s.Mean < o.Mean - 8)
                problems.Add(Inv($"the {r.Key} region became very dark and muddy (L* {o.Mean:0}→{s.Mean:0}), unless this is a night or deliberately low-key scene"));
        return problems;
    }

    private static string Line(RegionStats s) => Inv(
        $"L* {s.Mean:0} (10–90%: {s.Low:0}–{s.High:0}), detail {s.Detail:0.0}, chroma {s.Chroma:0}{(s.Chroma > 6 ? $" ({HueName(s.Hue)})" : "")}, blown {s.Blown:0.#}%, crushed {s.Crushed:0.#}%");

    private static string HueName(double hue) => hue switch
    {
        < 20 => "magenta-red", < 55 => "red-orange", < 80 => "yellow", < 110 => "yellow-green", < 165 => "green",
        < 215 => "cyan-teal", < 290 => "blue", < 330 => "purple", _ => "magenta-red",
    };

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // ── Картинка з підписами ──────────────────────────────────────────────────

    private static readonly Dictionary<string, (byte R, byte G, byte B)> Colors = new()
    {
        ["sky"] = (60, 140, 255), ["subject"] = (255, 60, 60), ["water"] = (0, 220, 220), ["vegetation"] = (60, 220, 60),
        ["mountains"] = (200, 120, 255), ["architecture"] = (255, 160, 0), ["natural_ground"] = (220, 200, 80), ["artificial_ground"] = (160, 160, 160),
    };

    private static byte[] DrawOverlay(byte[] asShot, int w, int h, List<Region> regions)
    {
        var px = (byte[])asShot.Clone();
        // Тло трохи притемнене, ділянки — напівпрозорим кольором; об'єкт — останнім, поверх ландшафту.
        foreach (var r in regions.OrderBy(r => r.Key == "subject"))
        {
            var (cr, cg, cb) = Colors.GetValueOrDefault(r.Key, ((byte)255, (byte)255, (byte)255));
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float m = r.Mask[y * w + x];
                    if (m < 0.5f) continue;
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(px[o] * 0.5 + cb * 0.5);
                    px[o + 1] = (byte)(px[o + 1] * 0.5 + cg * 0.5);
                    px[o + 2] = (byte)(px[o + 2] * 0.5 + cr * 0.5);
                }
            });
        }
        using var image = new MagickImage(px, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaOption.Off);
        if (Math.Max(w, h) > AiStylist.ImageSide) image.Resize(new MagickGeometry(AiStylist.ImageSide, AiStylist.ImageSide));
        try
        {
            double size = Math.Max(image.Width, image.Height) / 38.0;
            foreach (var r in regions)
            {
                var drawables = new Drawables().Font("Arial").FontPointSize(size).TextAlignment(TextAlignment.Center)
                    .StrokeColor(MagickColors.Black).StrokeWidth(Math.Max(1, size / 10)).FillColor(MagickColors.White)
                    .Text(r.CenterX * image.Width, r.CenterY * image.Height + size / 3, r.Key);
                drawables.Draw(image);
            }
        }
        catch (MagickException) { } // без шрифту — лише кольори; назви є в тексті запиту
        image.Format = MagickFormat.Jpeg;
        image.Quality = 85;
        image.Strip();
        return image.ToByteArray();
    }
}
