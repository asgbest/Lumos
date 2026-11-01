using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lumos.Core;

/// <summary>
/// Від Sky до ArtificialGround — ШІ-маски (небо, головний об'єкт, ландшафт): готова картинка-маска від нейромережі + мазки пензля поверх.
/// ColorRange і LuminanceRange — маски за кольором і за яскравістю на весь кадр (див. <see cref="RangeMask"/>).
/// Нові види — лише в кінець: у файлах правок вид зберігається числом.
/// </summary>
public enum MaskKind
{
    Brush, Linear, Radial, Sky, Subject, Water, Vegetation, Mountains, Architecture, NaturalGround, ArtificialGround,
    ColorRange, LuminanceRange,
}

/// <summary>Точка на полотні кадру в частках 0…1 (до обрізки, тож маска лишається на місці, якщо змінити обрізку).</summary>
public readonly record struct MaskPoint(double X, double Y);

/// <summary>
/// Мазок пензля: розмір — діаметр у частках довшої сторони полотна, м'якість 0…1 (частка розмитого краю),
/// сила 0…1, гумка — стирає маску.
/// </summary>
public sealed record BrushStroke(double Size, double Feather, double Flow, bool Erase, EquatableList<MaskPoint> Points);

/// <summary>
/// Маска: де діють власні правки. На відміну від Lightroom, правки маски — повний набір інструментів
/// (світло, крива, колір, змішувач HSL, грейдування, ефекти, деталі), і вони відносні:
/// нулі — «як усе фото», +1 EV — на ступінь світліше за решту кадру тощо.
/// Лінійна: повна сила в точці 1, нуль — у точці 2. Радіальна: еліпс із центром у точці 1, півосі, кут і розмиття.
/// </summary>
public sealed record Mask
{
    public string Name { get; init; } = "";
    public MaskKind Kind { get; init; }
    public bool Visible { get; init; } = true;
    public bool Invert { get; init; }

    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public double RadiusX { get; init; } = 0.25;
    public double RadiusY { get; init; } = 0.25;
    public double Angle { get; init; }
    public double Feather { get; init; } = 50;

    public EquatableList<BrushStroke> Strokes { get; init; } = EquatableList<BrushStroke>.Empty;

    /// <summary>
    /// Для ШІ-масок — маска від нейромережі як PNG у відтінках сірого (base64), на все полотно до обрізки.
    /// Зберігається у файлі правок, щоб не перераховувати мережу при кожному відкритті фото.
    /// </summary>
    public string? Raster { get; init; }

    /// <summary>
    /// Обмеження за кольором і яскравістю — для будь-якої маски (як «Range Mask» у Lightroom): кольори з піпетки,
    /// допуск 0…100; межі яскравості 0…100 і м'якість переходу 0…100. Без кольорів і з межами 0…100 — не обмежує.
    /// </summary>
    public EquatableList<RangeColor> RangeColors { get; init; } = EquatableList<RangeColor>.Empty;
    public double RangeColorAmount { get; init; } = 50;
    public double RangeLumMin { get; init; }
    public double RangeLumMax { get; init; } = 100;
    public double RangeLumSmooth { get; init; } = 50;

    [JsonIgnore]
    public bool HasRange => RangeColors.Count > 0 || RangeLumMin > 0 || RangeLumMax < 100;

    public bool IsAi => Kind is >= MaskKind.Sky and <= MaskKind.ArtificialGround;

    /// <summary>Маска за кольором чи яскравістю на весь кадр (доправляється пензлем).</summary>
    [JsonIgnore]
    public bool IsRange => Kind is MaskKind.ColorRange or MaskKind.LuminanceRange;

    /// <summary>Правки в масці (відносні; новий <see cref="DevelopSettings"/> — без змін).</summary>
    public DevelopSettings Local { get; init; } = new();

    public Mask DeepCopy() => this with { Local = Local.Copy() };

    public static Mask NewLinear(string name) => new() { Name = name, Kind = MaskKind.Linear, X1 = 0.5, Y1 = 0.05, X2 = 0.5, Y2 = 0.5 };
    public static Mask NewRadial(string name) => new() { Name = name, Kind = MaskKind.Radial, X1 = 0.5, Y1 = 0.5 };
    public static Mask NewBrush(string name) => new() { Name = name, Kind = MaskKind.Brush };
}

/// <summary>
/// Правки всередині маски: загальні правки фото + відносні правки маски.
/// Числа додаються (відносно «нейтральних» значень, тож «Змішування» 50 у масці не додає зайвих 50);
/// криві — спершу загальна, потім крива маски; колесо грейдування маски, якщо його задали, замінює загальне.
/// Геометрія — завжди загальна.
/// </summary>
public static class MaskSettings
{
    private static readonly JsonObject Neutral = JsonSerializer.SerializeToNode(new DevelopSettings())!.AsObject();
    private static readonly HashSet<string> GlobalOnly =
        ["Rotation", "FlipH", "Angle", "CropX", "CropY", "CropW", "CropH", "Masks", "Heals",
         "BlurAmount", "BlurRange", "BlurBokeh", "BlurFocusX", "BlurFocusY", "DepthMap",
         "LensProfile", "LensDistortion", "LensCaRed", "LensCaBlue", "LensVignette", "LensVignetteMid",
         "PerspVertical", "PerspHorizontal", "PerspRotate", "PerspAspect", "PerspScale", "PerspX", "PerspY"];

    public static DevelopSettings Combine(DevelopSettings global, DevelopSettings local)
    {
        var g = JsonSerializer.SerializeToNode(global with { Masks = EquatableList<Mask>.Empty })!.AsObject();
        var l = JsonSerializer.SerializeToNode(local with { Masks = EquatableList<Mask>.Empty })!.AsObject();
        foreach (var key in l.Select(p => p.Key).ToList())
        {
            if (GlobalOnly.Contains(key)) continue;
            if (key.StartsWith("Curve", StringComparison.Ordinal))
                g[key] = ComposeCurves(g[key], l[key]);
            else if (key.StartsWith("Grade", StringComparison.Ordinal) && l[key] is JsonObject wheel)
                g[key] = CombineWheel(g[key]!.AsObject(), wheel);
            else
                g[key] = Add(g[key], l[key], Neutral[key]);
        }
        return g.Deserialize<DevelopSettings>() ?? global;
    }

    private static JsonNode? Add(JsonNode? global, JsonNode? local, JsonNode? neutral)
    {
        if (global is JsonObject go && local is JsonObject lo)
        {
            var result = go.DeepClone().AsObject();
            foreach (var (k, v) in lo) result[k] = Add(go[k], v, (neutral as JsonObject)?[k]);
            return result;
        }
        if (global is JsonValue gv && local is JsonValue lv && gv.TryGetValue(out double a) && lv.TryGetValue(out double b))
        {
            double n = neutral is JsonValue nv && nv.TryGetValue(out double x) ? x : 0;
            return b == n ? gv.DeepClone() : JsonValue.Create(a + b - n);
        }
        return global?.DeepClone();
    }

    private static JsonNode? CombineWheel(JsonObject global, JsonObject local)
    {
        double sat = local["Saturation"]?.GetValue<double>() ?? 0;
        var result = (sat > 0 ? local : global).DeepClone().AsObject();
        result["Luminance"] = (global["Luminance"]?.GetValue<double>() ?? 0) + (local["Luminance"]?.GetValue<double>() ?? 0);
        return result;
    }

    private static JsonNode? ComposeCurves(JsonNode? global, JsonNode? local)
    {
        var g = global?.Deserialize<Curve>() ?? Curve.Identity;
        var l = local?.Deserialize<Curve>() ?? Curve.Identity;
        if (l.IsIdentity) return global?.DeepClone();
        if (g.IsIdentity) return local?.DeepClone();
        const int samples = 17;
        var points = Enumerable.Range(0, samples).Select(i =>
        {
            double x = i / (double)(samples - 1);
            return new CurvePoint(x, l.Evaluate(g.Evaluate(x)));
        }).ToList();
        return JsonSerializer.SerializeToNode(new Curve(points));
    }
}

/// <summary>
/// Сила маски в кожному пікселі (0…1) для зображення, що рахується: враховує обрізку (маска прив'язана до полотна)
/// і те, яку частину кадру рахуємо (масштаб 100 %).
/// </summary>
public static class MaskShapes
{
    /// <param name="image">Зображення, що рахується (для обмеження за кольором і яскравістю), і як його «бачить» маска.</param>
    public static float[] Rasterize(Mask mask, DevelopSettings s, RenderFrame frame, int width, int height,
        LinearImage? image = null, RangeLook look = default)
    {
        var weights = new float[width * height];
        double frameW = frame.FrameWidth > 0 ? frame.FrameWidth : width, frameH = frame.FrameHeight > 0 ? frame.FrameHeight : height;
        // Полотно (до обрізки) у пікселях цього зображення: кадр — це його частина CropW × CropH.
        double canvasW = frameW / Math.Max(1e-6, s.CropW), canvasH = frameH / Math.Max(1e-6, s.CropH);
        // Піксель цього зображення → точка полотна: + зсув обрізки + зсув ділянки в кадрі.
        double offsetX = s.CropX * canvasW + frame.OffsetX, offsetY = s.CropY * canvasH + frame.OffsetY;

        switch (mask.Kind)
        {
            case MaskKind.Linear: Linear(mask, weights, width, height, canvasW, canvasH, offsetX, offsetY); break;
            case MaskKind.Radial: Radial(mask, weights, width, height, canvasW, canvasH, offsetX, offsetY); break;
            case MaskKind.ColorRange or MaskKind.LuminanceRange:
                Array.Fill(weights, 1f); // увесь кадр, пензлем (гумкою) — прибрати зайве
                Brush(mask, weights, width, height, canvasW, canvasH, offsetX, offsetY);
                break;
            case >= MaskKind.Sky:
                FromRaster(mask, weights, width, height, canvasW, canvasH, offsetX, offsetY);
                Brush(mask, weights, width, height, canvasW, canvasH, offsetX, offsetY); // доправлення пензлем поверх
                break;
            default: Brush(mask, weights, width, height, canvasW, canvasH, offsetX, offsetY); break;
        }
        // Інверсія: у масці за кольором — «усе, крім цього кольору»; в інших — форма навпаки, а колір/яскравість
        // далі обмежують уже її (напр. «поза еліпсом, але лише небо»).
        if (mask.Invert && !mask.IsRange) Invert(weights);
        if (image is not null && look.Profile is not null && image.Width == width && image.Height == height
            && RangeMask.Weights(mask, image, look, frame) is { } range)
        {
            if (mask.Invert && mask.IsRange) Invert(range);
            for (int i = 0; i < weights.Length; i++) weights[i] *= range[i];
        }
        else if (mask.Invert && mask.IsRange) Invert(weights);
        return weights;
    }

    private static void Invert(float[] weights)
    {
        for (int i = 0; i < weights.Length; i++) weights[i] = 1 - weights[i];
    }

    private static void Linear(Mask m, float[] w, int width, int height, double cw, double ch, double ox, double oy)
    {
        double x1 = m.X1 * cw, y1 = m.Y1 * ch, x2 = m.X2 * cw, y2 = m.Y2 * ch;
        double dx = x2 - x1, dy = y2 - y1, len2 = Math.Max(1e-9, dx * dx + dy * dy);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                // Точка пікселя на полотні → наскільки вона «пройшла» від точки 1 до точки 2.
                double px = x + 0.5 + ox, py = y + 0.5 + oy;
                double t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / len2, 0, 1);
                w[y * width + x] = (float)(1 - t * t * (3 - 2 * t));
            }
        });
    }

    private static void Radial(Mask m, float[] w, int width, int height, double cw, double ch, double ox, double oy)
    {
        double cx = m.X1 * cw, cy = m.Y1 * ch;
        double rx = Math.Max(1, m.RadiusX * cw), ry = Math.Max(1, m.RadiusY * ch);
        double a = m.Angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double inner = 1 - Math.Clamp(m.Feather / 100, 0.01, 1);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 + ox - cx, py = y + 0.5 + oy - cy;
                double u = (px * cos + py * sin) / rx, v = (-px * sin + py * cos) / ry;
                double d = Math.Sqrt(u * u + v * v);
                double t = Math.Clamp((d - inner) / (1 - inner), 0, 1);
                w[y * width + x] = (float)(1 - t * t * (3 - 2 * t));
            }
        });
    }

    private static void FromRaster(Mask m, float[] w, int width, int height, double cw, double ch, double ox, double oy)
    {
        if (m.Raster is null || MaskRaster.Decode(m.Raster) is not { } raster) return;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                // Піксель → точка полотна (0…1) → піксель маски.
                double u = (x + 0.5 + ox) / cw, v = (y + 0.5 + oy) / ch;
                w[y * width + x] = AiSegmenter.SampleFloat(raster.Data, raster.Width, raster.Height,
                    u * raster.Width - 0.5, v * raster.Height - 0.5);
            }
        });
    }

    /// <summary>Мазки — як відбитки круглого м'якого пензля через рівні проміжки вздовж лінії.</summary>
    private static void Brush(Mask m, float[] w, int width, int height, double cw, double ch, double ox, double oy)
    {
        double longSide = Math.Max(cw, ch);
        foreach (var stroke in m.Strokes)
        {
            double radius = Math.Max(0.5, stroke.Size * longSide / 2);
            double hard = radius * (1 - Math.Clamp(stroke.Feather, 0, 1));
            float flow = (float)Math.Clamp(stroke.Flow, 0, 1);
            var dabs = Dabs(stroke.Points, cw, ch, Math.Max(1, radius * 0.25));
            // Мазок спершу малюється окремо (максимум відбитків), щоб у місцях перекриття не «темніло» —
            // а потім цілим накладається на маску (чи стирає її).
            var layer = new float[width * height];
            foreach (var (dx, dy) in dabs)
            {
                double px = dx - ox, py = dy - oy;
                int x0 = Math.Max(0, (int)(px - radius)), x1 = Math.Min(width - 1, (int)(px + radius) + 1);
                int y0 = Math.Max(0, (int)(py - radius)), y1 = Math.Min(height - 1, (int)(py + radius) + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double d = Math.Sqrt((x + 0.5 - px) * (x + 0.5 - px) + (y + 0.5 - py) * (y + 0.5 - py));
                        if (d >= radius) continue;
                        double t = d <= hard ? 0 : (d - hard) / Math.Max(1e-6, radius - hard);
                        float value = (float)(1 - t * t * (3 - 2 * t));
                        int i = y * width + x;
                        if (value > layer[i]) layer[i] = value;
                    }
            }
            for (int i = 0; i < w.Length; i++)
            {
                float a = layer[i] * flow;
                if (a <= 0) continue;
                w[i] = stroke.Erase ? w[i] * (1 - a) : w[i] + (1 - w[i]) * a;
            }
        }
    }

    internal static List<(double X, double Y)> Dabs(IReadOnlyList<MaskPoint> points, double cw, double ch, double spacing)
    {
        var result = new List<(double, double)>();
        for (int i = 0; i < points.Count; i++)
        {
            double x = points[i].X * cw, y = points[i].Y * ch;
            if (i == 0) { result.Add((x, y)); continue; }
            double px = points[i - 1].X * cw, py = points[i - 1].Y * ch;
            double len = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            int steps = Math.Max(1, (int)(len / spacing));
            for (int k = 1; k <= steps; k++) result.Add((px + (x - px) * k / steps, py + (y - py) * k / steps));
        }
        return result;
    }
}
