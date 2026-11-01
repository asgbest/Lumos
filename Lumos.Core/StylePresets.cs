using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lumos.Core;

/// <summary>
/// Стиль (пресет): набір правок, який накладається на фото. Містить лише ті параметри, що в ньому задані,
/// тож решта правок фото (і обрізка) лишаються як були.
/// <see cref="Settings"/> — частина <see cref="DevelopSettings"/> у JSON (ті самі назви полів).
/// <see cref="Skipped"/> — що було в оригінальному пресеті, але Lumos поки не вміє (для чесного звіту).
/// </summary>
public sealed record Style(string Name, string Group, JsonObject Settings, IReadOnlyList<string> Skipped);

/// <summary>
/// Стилі: власний формат <c>.lumosstyle</c> (JSON) та імпорт пресетів Lightroom / Camera Raw —
/// <c>.xmp</c>, старі <c>.lrtemplate</c> і <c>.zip</c>-пакети з ними.
/// </summary>
public static class StylePresets
{
    public const string Extension = ".lumosstyle";
    public static readonly string[] ImportExtensions = ["xmp", "lrtemplate", "lumosstyle", "lumenstyle", "zip"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Геометрія, маски й латки — властивість конкретного кадру, у власний стиль не потрапляють (як і в Lightroom).
    private static readonly string[] GeometryKeys =
        [nameof(DevelopSettings.Rotation), nameof(DevelopSettings.FlipH), nameof(DevelopSettings.Angle),
         nameof(DevelopSettings.CropX), nameof(DevelopSettings.CropY), nameof(DevelopSettings.CropW), nameof(DevelopSettings.CropH),
         nameof(DevelopSettings.PerspVertical), nameof(DevelopSettings.PerspHorizontal), nameof(DevelopSettings.PerspRotate),
         nameof(DevelopSettings.PerspAspect), nameof(DevelopSettings.PerspScale), nameof(DevelopSettings.PerspX), nameof(DevelopSettings.PerspY),
         nameof(DevelopSettings.Masks), nameof(DevelopSettings.Heals),
         nameof(DevelopSettings.BlurFocusX), nameof(DevelopSettings.BlurFocusY), nameof(DevelopSettings.DepthMap)];

    /// <summary>Групи правок — для вибору, що копіювати з фото на фото (як у Lightroom).</summary>
    public static readonly (string Name, string[] Keys)[] SettingGroups =
    [
        ("☀️ Світло", ["Exposure", "Contrast", "Highlights", "Shadows", "Whites", "Blacks"]),
        ("📈 Тонова крива", ["CurveMaster", "CurveRed", "CurveGreen", "CurveBlue"]),
        ("🎨 Колір", ["Temperature", "Tint", "Vibrance", "Saturation"]),
        ("🌈 Змішувач кольорів", ["HslRed", "HslOrange", "HslYellow", "HslGreen", "HslAqua", "HslBlue", "HslPurple", "HslMagenta"]),
        ("🎡 Кольорове грейдування", ["GradeShadows", "GradeMidtones", "GradeHighlights", "GradeGlobal", "GradeBlending", "GradeBalance"]),
        ("✨ Ефекти", ["Texture", "Clarity", "Dehaze", "VignetteAmount", "VignetteMidpoint", "VignetteFeather", "VignetteRoundness",
            "GrainAmount", "GrainSize", "GrainRoughness"]),
        ("🔍 Деталі", ["SharpenAmount", "SharpenRadius", "SharpenDetail", "SharpenMasking", "NoiseReduction", "ColorNoiseReduction"]),
        (GeometryGroup, ["Rotation", "FlipH", "Angle", "CropX", "CropY", "CropW", "CropH"]),
        ("🔬 Об'єктив", ["LensProfile", "LensDistortion", "LensCaRed", "LensCaBlue", "LensVignette", "LensVignetteMid"]),
        (PerspectiveGroup, ["PerspVertical", "PerspHorizontal", "PerspRotate", "PerspAspect", "PerspScale", "PerspX", "PerspY"]),
        (MasksGroup, ["Masks"]),
        (HealGroup, ["Heals"]),
        ("🔭 Розмиття об'єктива", ["BlurAmount", "BlurRange", "BlurBokeh"]),
    ];

    public const string MasksGroup = "🖌 Маски";

    public const string HealGroup = "🩹 Відновлення";

    public const string GeometryGroup = "✂️ Обрізка й поворот";

    public const string PerspectiveGroup = "📐 Перспектива";

    /// <summary>Накладає стиль: задані в ньому параметри замінюють поточні, решта не змінюється.</summary>
    public static DevelopSettings Apply(DevelopSettings current, Style style)
    {
        var node = JsonSerializer.SerializeToNode(current)!.AsObject();
        Merge(node, style.Settings);
        if (node.Deserialize<DevelopSettings>() is not { } applied) return current;
        applied.LensProfileData = current.LensProfileData; // числа профілю — від цього фото, у стилях їх немає
        return applied;
    }

    /// <summary>
    /// Стиль із силою, як «Amount» у Lightroom: 0 — без змін, 1 — як у стилі, 2 — удвічі сильніше.
    /// Числа змішуються між «до» і «зі стилем», криві — по точках; відтінок коліс грейдування не змішується (це кут).
    /// </summary>
    public static DevelopSettings Apply(DevelopSettings current, Style style, double amount)
    {
        var applied = Apply(current, style);
        if (Math.Abs(amount - 1) < 1e-9) return applied;
        var from = JsonSerializer.SerializeToNode(current)!.AsObject();
        var to = JsonSerializer.SerializeToNode(applied)!.AsObject();
        Blend(from, to, amount, "");
        if (to.Deserialize<DevelopSettings>() is not { } blended) return applied;
        blended.LensProfileData = current.LensProfileData;
        return blended;
    }

    private static void Blend(JsonObject from, JsonObject to, double amount, string parent)
    {
        foreach (var key in to.Select(p => p.Key).ToList())
        {
            var source = from[key];
            var target = to[key];
            if (target is JsonObject targetObject && source is JsonObject sourceObject)
            {
                if (key.StartsWith("Curve", StringComparison.Ordinal)) to[key] = BlendCurve(sourceObject, targetObject, amount);
                else Blend(sourceObject, targetObject, amount, key);
            }
            else if (target is JsonValue t && source is JsonValue s && t.TryGetValue(out double tn) && s.TryGetValue(out double sn)
                     && tn != sn && !(parent.StartsWith("Grade", StringComparison.Ordinal) && key == "Hue"))
            {
                to[key] = sn + (tn - sn) * amount;
            }
        }
    }

    private static JsonNode? BlendCurve(JsonObject from, JsonObject to, double amount)
    {
        var a = from.Deserialize<Curve>() ?? Curve.Identity;
        var b = to.Deserialize<Curve>() ?? Curve.Identity;
        const int samples = 17;
        var points = new List<CurvePoint>(samples);
        for (int i = 0; i < samples; i++)
        {
            double x = i / (double)(samples - 1), ya = a.Evaluate(x);
            points.Add(new CurvePoint(x, ya + (b.Evaluate(x) - ya) * amount));
        }
        return JsonSerializer.SerializeToNode(new Curve(points));
    }

    /// <summary>Стиль лише з вибраних груп правок (для «Копіювати правки»; обрізку теж можна, якщо її вибрали).</summary>
    public static Style FromSettings(string name, DevelopSettings s, IReadOnlyCollection<string> groups)
    {
        var keys = SettingGroups.Where(g => groups.Contains(g.Name)).SelectMany(g => g.Keys).ToHashSet();
        var node = JsonSerializer.SerializeToNode(s)!.AsObject();
        foreach (var key in node.Select(p => p.Key).Where(k => !keys.Contains(k)).ToList()) node.Remove(key);
        return new Style(name, "", node, []);
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sub && target[key] is JsonObject existing) Merge(existing, sub);
            // Маски стилю додаються до тих, що вже є на фото (як у Lightroom), а не замінюють їх.
            else if (key == nameof(DevelopSettings.Masks) && value is JsonArray added && target[key] is JsonArray current)
                target[key] = new JsonArray([.. current.Select(n => n?.DeepClone()), .. added.Select(n => n?.DeepClone())]);
            else target[key] = value?.DeepClone();
        }
    }

    /// <summary>Стиль із поточних правок: усе, крім геометрії.</summary>
    public static Style FromSettings(string name, DevelopSettings s)
    {
        var node = JsonSerializer.SerializeToNode(s)!.AsObject();
        foreach (var key in GeometryKeys) node.Remove(key);
        return new Style(name, "", node, []);
    }

    public static void Save(Style style, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(style, Json));

    /// <summary>Читає файл (або всі стилі з .zip). Помилкові файли пропускає й повертає їх список окремо.</summary>
    public static (List<Style> Styles, List<string> Failed) Import(string path)
    {
        var styles = new List<Style>();
        var failed = new List<string>();
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                string ext = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
                if (entry.Length == 0 || ext is not ("xmp" or "lrtemplate" or "lumosstyle" or "lumenstyle")) continue;
                using var reader = new StreamReader(entry.Open());
                TryParse(entry.Name, reader.ReadToEnd(), styles, failed);
            }
        }
        else
        {
            TryParse(Path.GetFileName(path), File.ReadAllText(path), styles, failed);
        }
        return (styles, failed);
    }

    private static void TryParse(string fileName, string content, List<Style> styles, List<string> failed)
    {
        try
        {
            var style = Parse(fileName, content);
            if (style is not null) styles.Add(style); else failed.Add(fileName);
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or FormatException)
        {
            failed.Add(fileName);
        }
    }

    public static Style? Parse(string fileName, string content)
    {
        string fallbackName = Path.GetFileNameWithoutExtension(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".lumosstyle" or ".lumenstyle" => JsonSerializer.Deserialize<Style>(content, Json), // .lumenstyle — стара назва
            ".lrtemplate" => FromLrTemplate(content, fallbackName),
            _ => FromXmp(content, fallbackName),
        };
    }

    // ── Lightroom / Camera Raw ─────────────────────────────────────────────────

    private const string Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>Пресет .xmp: параметри — атрибути crs:…, криві й назва — вкладені елементи.</summary>
    private static Style? FromXmp(string xml, string fallbackName)
    {
        var doc = XDocument.Parse(xml);
        XNamespace crs = Crs, rdf = Rdf;
        var description = doc.Descendants(rdf + "Description")
            .FirstOrDefault(d => d.Attributes().Any(a => a.Name.Namespace == crs) || d.Elements().Any(e => e.Name.Namespace == crs));
        if (description is null) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in description.Attributes().Where(a => a.Name.Namespace == crs)) values[a.Name.LocalName] = a.Value;
        var curves = new Dictionary<string, List<(double X, double Y)>>();
        var nested = new HashSet<string>();
        foreach (var e in description.Elements().Where(e => e.Name.Namespace == crs))
        {
            nested.Add(e.Name.LocalName);
            var items = e.Descendants(rdf + "li").Select(li => li.Value.Trim()).ToList();
            if (e.Element(rdf + "Seq") is not null && e.Name.LocalName.StartsWith("ToneCurve"))
                curves[e.Name.LocalName] = items.Select(ParsePoint).OfType<(double, double)>().ToList();
            else if (items.Count > 0)
                values[e.Name.LocalName] = items[0];
            else if (!e.HasElements)
                values[e.Name.LocalName] = e.Value.Trim();
        }

        string name = values.GetValueOrDefault("Name") is { Length: > 0 } n ? n : fallbackName;
        string group = values.GetValueOrDefault("Group") ?? "";
        var extraSkipped = new List<string>();
        if (nested.Contains("Look")) extraSkipped.Add("Профіль (Look)");
        var masks = ImportMasks(description, out int skippedMasks);
        if (skippedMasks > 0 || nested.Contains("PaintBasedCorrections"))
            extraSkipped.Add("ШІ-маски й складні маски");
        var style = Convert(name, group, values, curves, extraSkipped);
        if (masks.Count > 0) style.Settings[nameof(DevelopSettings.Masks)] = JsonSerializer.SerializeToNode(masks);
        return style;

        static (double, double)? ParsePoint(string text)
        {
            var parts = text.Split(',');
            return parts.Length == 2 && TryNumber(parts[0], out double x) && TryNumber(parts[1], out double y) ? (x, y) : null;
        }
    }

    /// <summary>Старий формат Lightroom Classic: Lua-таблиця <c>settings = { Exposure2012 = 0.5, ToneCurvePV2012 = { 0, 0, 255, 255 }, … }</c>.</summary>
    private static Style? FromLrTemplate(string text, string fallbackName)
    {
        int start = text.IndexOf("settings", StringComparison.Ordinal);
        if (start < 0) return null;
        string body = text[start..];

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var curves = new Dictionary<string, List<(double X, double Y)>>();
        foreach (Match m in Regex.Matches(body, @"(\w+)\s*=\s*\{([\d\s,.\-+]*)\}"))
        {
            var numbers = m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => TryNumber(s, out double v) ? v : double.NaN).Where(v => !double.IsNaN(v)).ToList();
            var points = new List<(double, double)>();
            for (int i = 0; i + 1 < numbers.Count; i += 2) points.Add((numbers[i], numbers[i + 1]));
            curves[m.Groups[1].Value] = points;
        }
        foreach (Match m in Regex.Matches(body, @"(\w+)\s*=\s*(-?[\d.]+|true|false|""[^""]*"")"))
            values.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim('"'));

        var title = Regex.Match(text, @"title\s*=\s*(?:ZSTR\s*)?""([^""]*)""");
        string name = title.Success && title.Groups[1].Value.Length > 0 ? title.Groups[1].Value : fallbackName;
        return Convert(name, "", values, curves, []);
    }

    private static readonly (string Crs, string Ours)[] Direct =
    [
        ("Exposure2012", nameof(DevelopSettings.Exposure)),
        ("Contrast2012", nameof(DevelopSettings.Contrast)),
        ("Highlights2012", nameof(DevelopSettings.Highlights)),
        ("Shadows2012", nameof(DevelopSettings.Shadows)),
        ("Whites2012", nameof(DevelopSettings.Whites)),
        ("Blacks2012", nameof(DevelopSettings.Blacks)),
        ("Vibrance", nameof(DevelopSettings.Vibrance)),
        ("Saturation", nameof(DevelopSettings.Saturation)),
        ("IncrementalTemperature", nameof(DevelopSettings.Temperature)),
        ("IncrementalTint", nameof(DevelopSettings.Tint)),
        ("ColorGradeBlending", nameof(DevelopSettings.GradeBlending)),
        ("SplitToningBalance", nameof(DevelopSettings.GradeBalance)),
        ("Texture", nameof(DevelopSettings.Texture)),
        ("Clarity2012", nameof(DevelopSettings.Clarity)),
        ("Dehaze", nameof(DevelopSettings.Dehaze)),
        ("LensManualDistortionAmount", nameof(DevelopSettings.LensDistortion)),
        ("VignetteAmount", nameof(DevelopSettings.LensVignette)),
        ("VignetteMidpoint", nameof(DevelopSettings.LensVignetteMid)),
        ("PostCropVignetteAmount", nameof(DevelopSettings.VignetteAmount)),
        ("PostCropVignetteMidpoint", nameof(DevelopSettings.VignetteMidpoint)),
        ("PostCropVignetteFeather", nameof(DevelopSettings.VignetteFeather)),
        ("PostCropVignetteRoundness", nameof(DevelopSettings.VignetteRoundness)),
        ("GrainAmount", nameof(DevelopSettings.GrainAmount)),
        ("GrainSize", nameof(DevelopSettings.GrainSize)),
        ("GrainFrequency", nameof(DevelopSettings.GrainRoughness)),
        ("Sharpness", nameof(DevelopSettings.SharpenAmount)),
        ("SharpenRadius", nameof(DevelopSettings.SharpenRadius)),
        ("SharpenDetail", nameof(DevelopSettings.SharpenDetail)),
        ("SharpenEdgeMasking", nameof(DevelopSettings.SharpenMasking)),
        ("LuminanceSmoothing", nameof(DevelopSettings.NoiseReduction)),
        ("ColorNoiseReduction", nameof(DevelopSettings.ColorNoiseReduction)),
    ];

    private static readonly string[] HslColors = ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];

    // Що Lumos поки не вміє: назва Lightroom, типове значення (його не вважаємо правкою), назва українською.
    private static readonly (string Key, double Default, string Name)[] Unsupported =
    [
        ("ShadowTint", 0, "Калібрування"),
        ("RedHue", 0, "Калібрування"), ("RedSaturation", 0, "Калібрування"),
        ("GreenHue", 0, "Калібрування"), ("GreenSaturation", 0, "Калібрування"),
        ("BlueHue", 0, "Калібрування"), ("BlueSaturation", 0, "Калібрування"),
    ];

    /// <summary>Перекладає параметри Lightroom (crs:…) у правки Lumos.</summary>
    /// <summary>
    /// Маски пресета Lightroom (MaskGroupBasedCorrections): переносимо ті, що складаються з одного лінійного
    /// або радіального градієнта. ШІ-маски (небо, об'єкт), пензель і маски за діапазоном поки пропускаємо.
    /// Локальні правки Lightroom записує в частках: −1…1 — це −100…+100 (експозиція — −4…+4 EV).
    /// </summary>
    private static List<Mask> ImportMasks(XElement description, out int skipped)
    {
        XNamespace crs = Crs, rdf = Rdf;
        var result = new List<Mask>();
        skipped = 0;
        var group = description.Element(crs + "MaskGroupBasedCorrections");
        if (group is null) return result;

        foreach (var correction in group.Descendants(rdf + "Description").Where(d => (string?)d.Attribute(crs + "What") == "Correction"))
        {
            var shapes = correction.Element(crs + "CorrectionMasks")?.Descendants(rdf + "li").ToList() ?? [];
            if (shapes.Count != 1 || (string?)correction.Attribute(crs + "CorrectionActive") == "false")
            {
                skipped++;
                continue;
            }
            var shape = shapes[0];
            double A(XElement e, string key, double fallback = 0) =>
                e.Attribute(crs + key) is { } a && TryNumber(a.Value, out double v) ? v : fallback;

            string what = (string?)shape.Attribute(crs + "What") ?? "";
            bool inverted = (string?)shape.Attribute(crs + "MaskInverted") == "true";
            string name = (string?)correction.Attribute(crs + "CorrectionName") ?? "Маска";
            Mask mask;
            if (what == "Mask/Gradient")
            {
                mask = Mask.NewLinear(name) with
                {
                    X1 = A(shape, "FullX"), Y1 = A(shape, "FullY"), X2 = A(shape, "ZeroX"), Y2 = A(shape, "ZeroY"), Invert = inverted,
                };
            }
            else if (what == "Mask/CircularGradient")
            {
                double top = A(shape, "Top"), left = A(shape, "Left"), bottom = A(shape, "Bottom"), right = A(shape, "Right");
                // «Flipped» — дія всередині еліпса; інакше Lightroom діє зовні.
                bool inside = (string?)shape.Attribute(crs + "Flipped") == "true";
                mask = Mask.NewRadial(name) with
                {
                    X1 = (left + right) / 2, Y1 = (top + bottom) / 2,
                    RadiusX = Math.Max(0.005, (right - left) / 2), RadiusY = Math.Max(0.005, (bottom - top) / 2),
                    Angle = A(shape, "Angle"), Feather = A(shape, "Feather", 50), Invert = inside == inverted,
                };
            }
            else
            {
                skipped++;
                continue;
            }

            double amount = A(correction, "CorrectionAmount", 1);
            double P(string key) => Math.Round(A(correction, key) * 100 * amount, 1);
            var local = new DevelopSettings
            {
                Exposure = Math.Round(A(correction, "LocalExposure2012") * 4 * amount, 2),
                Contrast = P("LocalContrast2012"),
                Highlights = P("LocalHighlights2012"),
                Shadows = P("LocalShadows2012"),
                Whites = P("LocalWhites2012"),
                Blacks = P("LocalBlacks2012"),
                Clarity = P("LocalClarity2012"),
                Texture = P("LocalTexture"),
                Dehaze = P("LocalDehaze"),
                Temperature = P("LocalTemperature"),
                Tint = P("LocalTint"),
                Saturation = P("LocalSaturation"),
                SharpenAmount = Math.Max(0, P("LocalSharpness")),
                NoiseReduction = Math.Max(0, P("LocalLuminanceNoise")),
                GrainAmount = Math.Max(0, P("LocalGrain")),
            };
            if (A(correction, "LocalToningSaturation") is var toning and > 0)
                local.GradeGlobal = new GradeWheel(A(correction, "LocalToningHue"), Math.Round(toning * 100 * amount), 0);
            result.Add(mask with { Local = local });
        }
        return result;
    }

    private static Style Convert(string name, string group, Dictionary<string, string> v,
        Dictionary<string, List<(double X, double Y)>> curves, List<string> skipped)
    {
        var o = new JsonObject();

        foreach (var (crsKey, ours) in Direct)
            if (Number(v, crsKey) is { } value) o[ours] = value;

        // Абсолютний баланс білого (кельвіни) Lumos задає відносно камери — переводимо наближено.
        if (v.GetValueOrDefault("WhiteBalance") is not (null or "As Shot" or "Auto") && !v.ContainsKey("IncrementalTemperature"))
        {
            if (Number(v, "Temperature") is { } kelvin && kelvin > 1000)
                o[nameof(DevelopSettings.Temperature)] = Math.Round(Math.Clamp((1e6 / 5500 - 1e6 / kelvin) * 2, -100, 100));
            if (Number(v, "Tint") is { } tint)
                o[nameof(DevelopSettings.Tint)] = Math.Round(Math.Clamp(tint * 2 / 3, -100, 100));
            skipped.Add("Баланс білого (перенесено наближено)");
        }

        if (v.GetValueOrDefault("ConvertToGrayscale") is "True" or "true")
            o[nameof(DevelopSettings.Saturation)] = -100;

        // Змішувач кольорів
        foreach (var color in HslColors)
        {
            var band = new JsonObject();
            if (Number(v, "HueAdjustment" + color) is { } h) band["Hue"] = h;
            if (Number(v, "SaturationAdjustment" + color) is { } s) band["Saturation"] = s;
            if (Number(v, "LuminanceAdjustment" + color) is { } l) band["Luminance"] = l;
            if (band.Count > 0) o["Hsl" + color] = band;
        }

        // Кольорове грейдування: тіні й світлі в Lightroom лежать у старих полях SplitToning*.
        Wheel(nameof(DevelopSettings.GradeShadows), "SplitToningShadowHue", "SplitToningShadowSaturation", "ColorGradeShadowLum");
        Wheel(nameof(DevelopSettings.GradeMidtones), "ColorGradeMidtoneHue", "ColorGradeMidtoneSat", "ColorGradeMidtoneLum");
        Wheel(nameof(DevelopSettings.GradeHighlights), "SplitToningHighlightHue", "SplitToningHighlightSaturation", "ColorGradeHighlightLum");
        Wheel(nameof(DevelopSettings.GradeGlobal), "ColorGradeGlobalHue", "ColorGradeGlobalSat", "ColorGradeGlobalLum");

        // Криві: точкова (+ параметрична, згорнута в неї) і по каналах.
        var master = CurveFrom(curves.GetValueOrDefault("ToneCurvePV2012"));
        var parametric = Parametric(v);
        if (parametric is not null) master = Compose(parametric, master ?? Curve.Identity);
        if (master is not null) o[nameof(DevelopSettings.CurveMaster)] = JsonSerializer.SerializeToNode(master);
        foreach (var (suffix, key) in new[] { ("Red", nameof(DevelopSettings.CurveRed)), ("Green", nameof(DevelopSettings.CurveGreen)), ("Blue", nameof(DevelopSettings.CurveBlue)) })
            if (CurveFrom(curves.GetValueOrDefault("ToneCurvePV2012" + suffix)) is { } c)
                o[key] = JsonSerializer.SerializeToNode(c);

        foreach (var (key, def, label) in Unsupported)
            if (Number(v, key) is { } value && Math.Abs(value - def) > 1e-9 && !skipped.Contains(label))
                skipped.Add(label);

        return new Style(name, group, o, skipped);

        void Wheel(string ours, string hueKey, string satKey, string lumKey)
        {
            var wheel = new JsonObject();
            if (Number(v, hueKey) is { } h) wheel["Hue"] = h;
            if (Number(v, satKey) is { } s) wheel["Saturation"] = s;
            if (Number(v, lumKey) is { } l) wheel["Luminance"] = l;
            if (wheel.Count > 0) o[ours] = wheel;
        }
    }

    /// <summary>Крива Lightroom (точки 0…255) → крива Lumos; пряма лінія — null (нема чого міняти).</summary>
    private static Curve? CurveFrom(List<(double X, double Y)>? points)
    {
        if (points is not { Count: >= 2 }) return null;
        var curve = new Curve(points.Select(p => new CurvePoint(p.X / 255, p.Y / 255)).ToList());
        return curve.IsIdentity ? null : curve;
    }

    /// <summary>
    /// Параметрична крива Lightroom (Тіні/Темні/Світлі/Світлі ділянки з межами зон) — наближено:
    /// кожен повзунок плавно піднімає чи опускає свою зону тонів.
    /// </summary>
    private static Func<double, double>? Parametric(Dictionary<string, string> v)
    {
        double[] amounts =
        [
            Number(v, "ParametricShadows") ?? 0, Number(v, "ParametricDarks") ?? 0,
            Number(v, "ParametricLights") ?? 0, Number(v, "ParametricHighlights") ?? 0,
        ];
        if (amounts.All(a => a == 0)) return null;
        double s1 = (Number(v, "ParametricShadowSplit") ?? 25) / 100;
        double s2 = (Number(v, "ParametricMidtoneSplit") ?? 50) / 100;
        double s3 = (Number(v, "ParametricHighlightSplit") ?? 75) / 100;
        double[] edges = [0, s1, s2, s3, 1];

        return x =>
        {
            double y = x;
            for (int i = 0; i < 4; i++)
            {
                // «Горб» над зоною i, що плавно заходить у сусідні зони й зникає на 0 та 1.
                double a = Math.Max(0, edges[i] - (edges[i + 1] - edges[i]) / 2);
                double b = Math.Min(1, edges[i + 1] + (edges[i + 1] - edges[i]) / 2);
                if (x <= a || x >= b) continue;
                double t = Math.Sin(Math.PI * (x - a) / (b - a));
                y += amounts[i] / 100 * 0.12 * t * t;
            }
            return Math.Clamp(y, 0, 1);
        };
    }

    /// <summary>Спершу параметрична крива, потім точкова — як у Lightroom; результат — одна точкова крива.</summary>
    private static Curve Compose(Func<double, double> first, Curve then)
    {
        const int samples = 17;
        var points = new List<CurvePoint>(samples);
        for (int i = 0; i < samples; i++)
        {
            double x = i / (double)(samples - 1);
            points.Add(new CurvePoint(x, then.Evaluate(first(x))));
        }
        return new Curve(points);
    }

    private static double? Number(Dictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var text) && TryNumber(text, out double value) ? value : null;

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().TrimStart('+'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
