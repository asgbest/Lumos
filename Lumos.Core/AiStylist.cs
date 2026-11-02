using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumos.Core;

/// <summary>
/// Маска, яку запропонував ШІ: вид (ШІ-маска, лінійна чи радіальна), її форма й відносні правки всередині.
/// Для ШІ-масок форму рахує вже сама програма (нейромережа на цьому комп'ютері).
/// </summary>
public sealed record AiMaskSuggestion(string Name, MaskKind Kind, bool Invert, Mask Shape, DevelopSettings Local);

/// <summary>
/// Варіант стилю від ШІ: назва, чому він пасує фото, загальні правки й маски.
/// Raw — сам варіант, як його написала модель (для перевірки на другому колі); Critique — що модель виправила після перевірки.
/// </summary>
public sealed record AiSuggestion(string Name, string Description, DevelopSettings Settings, IReadOnlyList<AiMaskSuggestion> Masks)
{
    public JsonObject Raw { get; init; } = [];
    public string? Critique { get; init; }
}

/// <summary>Частина запиту до Gemini: текст або фото (JPEG).</summary>
public sealed record GeminiPart(string? Text, byte[]? Jpeg = null);

/// <summary>
/// «✨ AI»: Gemini дивиться на фото й пропонує, у якій стилістиці його обробити, — одразу числами наших повзунків.
/// Тут — опис можливостей програми для моделі, запит і ретельна перевірка відповіді (невідомі поля відкидаються,
/// числа обрізаються до меж повзунків), щоб будь-яка відповідь давала коректні правки.
/// </summary>
public static class AiStylist
{
    // Порівняно на фото лісу: argon дає помітно сміливіші й різніші стилі, ніж 3.8-flash (той — швидший, але боязкий).
    public const string DefaultModel = "gemini-4-argon";
    public const int RenderSide = 768; // рендери варіантів, які модель перевіряє на другому колі

    /// <summary>Ключ Gemini: свій, якщо його задали у вікні «✨ AI», інакше вбудований у програму.</summary>
    public static string ApiKey(string? own) => string.IsNullOrWhiteSpace(own) ? GeminiKey.BuiltIn : own;
    public const int ImageSide = 1024;

    private sealed record Param(string Name, double Min, double Max, string Help);

    // Числові повзунки: назва в JSON (як у DevelopSettings), межі, що означає.
    private static readonly Param[] Scalars =
    [
        new("Exposure", -5, 5, "exposure in EV stops"),
        new("Contrast", -100, 100, "global contrast"),
        new("Highlights", -100, 100, "recover (−) or brighten (+) bright areas"),
        new("Shadows", -100, 100, "lift (+) or deepen (−) dark areas"),
        new("Whites", -100, 100, "white point"),
        new("Blacks", -100, 100, "black point (− = deeper blacks, + = faded/matte)"),
        new("Temperature", -100, 100, "white balance relative to the camera's as-shot WB: + warmer (yellow), − cooler (blue); ±100 ≈ ±2 stops of blue/yellow shift — usually stay within ±30"),
        new("Tint", -100, 100, "+ magenta, − green, relative to as-shot WB"),
        new("Vibrance", -100, 100, "saturation that protects already-saturated colors and skin"),
        new("Saturation", -100, 100, "global saturation (−100 = black & white)"),
        new("Texture", -100, 100, "fine detail contrast (− smooths skin)"),
        new("Clarity", -100, 100, "midtone local contrast (− = soft glow)"),
        new("Dehaze", -100, 100, "remove (+) or add (−) atmospheric haze"),
        new("VignetteAmount", -100, 100, "post-crop vignette: − darkens corners, + lightens"),
        new("VignetteMidpoint", 0, 100, "how far the vignette reaches (default 50)"),
        new("VignetteFeather", 0, 100, "vignette softness (default 50)"),
        new("VignetteRoundness", -100, 100, "vignette shape"),
        new("GrainAmount", 0, 100, "film grain strength"),
        new("GrainSize", 0, 100, "grain size (default 25)"),
        new("GrainRoughness", 0, 100, "grain roughness (default 50)"),
        new("GradeBlending", 0, 100, "color grading: overlap of shadow/highlight zones (default 50)"),
        new("GradeBalance", -100, 100, "color grading: − favors shadows, + favors highlights"),
        new("SharpenAmount", 0, 150, "sharpening"),
        new("NoiseReduction", 0, 100, "luminance noise reduction"),
        new("ColorNoiseReduction", 0, 100, "color noise reduction"),
    ];

    private static readonly string[] HslBands = ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];
    private static readonly string[] Wheels = ["Shadows", "Midtones", "Highlights", "Global"];
    private static readonly string[] Curves = ["Master", "Red", "Green", "Blue"];

    private static readonly Dictionary<string, MaskKind> MaskKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["linear"] = MaskKind.Linear, ["radial"] = MaskKind.Radial,
        ["sky"] = MaskKind.Sky, ["subject"] = MaskKind.Subject, ["water"] = MaskKind.Water, ["vegetation"] = MaskKind.Vegetation,
        ["mountains"] = MaskKind.Mountains, ["architecture"] = MaskKind.Architecture,
        ["natural_ground"] = MaskKind.NaturalGround, ["artificial_ground"] = MaskKind.ArtificialGround,
    };

    /// <summary>Скільки масок на варіант: звичайний режим — кілька, Pro — повна обробка за планом.</summary>
    public const int MaxMasks = 4, MaxMasksPro = 9;

    /// <summary>Інструкція для моделі: що вміє програма, які параметри й у якому форматі відповідати.</summary>
    /// <param name="pro">«✨ AI Pro»: варіанти втілюють план обробки (усі його локальні кроки — маски), а не 1–3 маски.</param>
    public static string SystemInstruction(IEnumerable<MaskKind> aiMaskKinds, int count, bool pro = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            You are an award-winning photo editor and film colorist working inside Lumos, a Lightroom-like RAW editor.
            The user attaches a photo rendered with neutral default processing ("as shot"). First really LOOK at it: subject, where the eye
            should go, light direction and quality, dominant colors, what is distracting (flat light, muddy greens, dull sky, clutter)
            and what is beautiful. Then design {{count}} looks for THIS specific photo.

            What makes a good answer:
            - Think like a skilled photographer editing their own shot, not like a filter app. The result must look like a real,
              well-made photograph: natural light, believable colors, clean skin and skies, detail in highlights and shadows.
            - Every look must be ATMOSPHERIC: it creates a mood and a feeling of place and time (a quiet foggy morning, warm late light,
              cold rainy dusk, nostalgic film memory…), not just a "corrected" photo. Atmosphere comes from light and color working
              together: shaping light with soft masks (glow where light falls, quieter edges), depth (lighter, softer, slightly hazy
              background vs. richer foreground), a coherent color mood from white balance and gentle grading, and a tone curve that
              sets the feeling (soft lifted blacks for calm/nostalgia, deeper contrast for drama). A plain "natural correction" is a failure.
            - Each look has a clear INTENT tailored to this image (the mood, what to emphasize, what to calm down, where to lead the eye),
              not a generic preset. Derive every look from what you actually see in this photo, its light and its colors.
            - The looks must have clearly DIFFERENT moods (e.g. warm and golden vs. cool and misty vs. deep and dramatic vs. soft film),
              distinguishable at thumbnail size, and every one of them must suit this photo. There is NO fixed set of look types. Do not add a black-and-white look
              unless the photo is clearly stronger in monochrome (graphic light, texture, strong shapes) — and then at most one.
            - Measured values: most edits live within ±40. Highlights below −60, Shadows above +50, Clarity/Dehaze above +30 or heavy
              vignettes are rarely needed and usually look processed — use them only with a specific reason. Do not crush blacks or
              make the image muddy and dark unless the photo is a night/low-key scene. Prefer correct exposure and white balance first.
            - Color: refine the photo's own palette with the HSL mixer and subtle color grading (typical grading saturation 5…20).
              Avoid heavy hue shifts of natural colors (greens, skin, sky) — at most ±20 hue unless the user asks for a stylized look.
            - Local masks are the main tool for atmosphere: an adjustment of a concrete object (a car, a person, an animal, a house)
              goes on a "subject" mask with that object's box — it follows the exact outline, so the object itself gets brighter,
              warmer or crisper without a halo on the road or the sky around it. Radial masks are for light and air, not objects:
              a glow where light falls, a pool of light around the subject, gently darker or calmer edges; linear and sky masks
              for the sky and the ground. {{(pro ? "As many as the edit plan needs" : "1–3 per look")}}, believable; radial and linear masks soft (feather 60+).
            Respect the user's wishes if given — they override these defaults (including style, strength and black-and-white).

            """);
        if (pro)
            sb.AppendLine("""
                PRO MODE. With the photo you also get: a REGION MAP made by Lumos's own segmentation networks (labeled overlay + exact
                measurements of every region), a −1.5 EV preview showing what the RAW holds in the highlights, and an EDIT PLAN written
                by a senior editor for this photo. Every look must implement the WHOLE plan, like a professional's layered edit:
                - every LOCAL step of the plan becomes its own mask (typically 4–8 masks per look): region steps use that region's key
                  as the mask kind (sky, mountains, water, vegetation, subject with its box…), light steps use radial masks,
                  foreground/top steps use linear masks; give each mask the Ukrainian name of its step;
                - the GLOBAL steps go into "settings" (white balance, tone, HSL palette, curve, grading);
                - the looks differ in MOOD and color (take the plan's moods, one per look), not in which steps they do — the strength
                  of each step follows the look's mood;
                - distractions from the plan are already removed by Lumos — do not try to hide them with masks.
                Use the region measurements: a blown sky that the RAW can recover needs strong sky Exposure/Highlights/Whites minus;
                a subject that should draw the eye must end up clearly brighter or richer than its surroundings.
                An underexposed or backlit RAW (dark land under a bright sky): open the land with Shadows and its own region masks
                (vegetation, mountains, ground, subject) rather than a huge global Exposure, keep true blacks (the darkest tones near
                L* 5–10, no milky fog unless it is the intent) and keep the sky's tone and clouds.

                """);
        AppendParameters(sb, aiMaskKinds, pro ? MaxMasksPro : MaxMasks - 1);
        sb.AppendLine("""
            Answer with JSON only, no markdown:
            {"looks": [{"name": "...", "intent": "...", "description": "...", "settings": {...}, "masks": [{"name": "...", "kind": "...", "invert": false,
              "x1": 0.5, "y1": 0.5, "x2": 0.5, "y2": 0.5, "radius_x": 0.3, "radius_y": 0.3, "angle": 0, "feather": 50, "adjustments": {...} } ] } ] }
            "intent" (English, 1–2 sentences): the precise visual goal of the look for this photo — it is used later to check the result.
            "name" (2–4 words) and "description" (1–2 sentences: what the look does and why it suits this photo) MUST be in Ukrainian.
            """);
        return sb.ToString();
    }

    /// <summary>Опис параметрів Lumos для моделі: порядок обробки, повзунки, криві, колеса, маски (спільне для фото й серії).</summary>
    internal static void AppendParameters(StringBuilder sb, IEnumerable<MaskKind> aiMaskKinds, int maxMasks = MaxMasks - 1)
    {
        sb.AppendLine($$"""
            Every look is expressed ONLY with Lumos parameters below; Lumos applies them automatically.
            Processing order (linear light): white balance → exposure → spatial effects (texture, clarity, dehaze) → HSL mixer →
            vibrance/saturation → tone (highlights, shadows, whites, blacks, contrast) + tone curves → color grading → vignette, grain.
            Because the HSL mixer runs BEFORE saturation, Saturation −100 plus HSL Luminance per color is a real B&W channel mixer
            (e.g. HslGreen Luminance −40 darkens foliage, HslOrange Luminance +30 brightens skin and autumn leaves).
            All values are ABSOLUTE; any parameter you omit stays at its neutral default (0 unless noted).

            Scale of the sliders (same as Lightroom): Exposure ±0.3 EV is a visible but gentle change; Highlights −20…−40 recovers
            a bright sky; Shadows +15…+35 opens dark areas; Clarity +10…+20 adds punch; Vignette −10…−25 is subtle.

            GLOBAL PARAMETERS ("settings" object):
            """);
        foreach (var p in Scalars) sb.AppendLine($"- {p.Name}: {p.Min}…{p.Max} — {p.Help}");
        sb.AppendLine($$"""
            - Hsl<Band> for Band in {{string.Join(", ", HslBands)}}: object {"Hue": −100…100, "Saturation": −100…100, "Luminance": −100…100} — per-color mixer.
            - Grade<Wheel> for Wheel in {{string.Join(", ", Wheels)}}: object {"Hue": 0…360 degrees, "Saturation": 0…100, "Luminance": −100…100} — color grading wheels
              (hue: 0 red, 30 orange, 60 yellow, 120 green, 180 cyan, 210 teal-blue, 240 blue, 300 magenta). Typical saturation 5…30.
            - Curve<Channel> for Channel in {{string.Join(", ", Curves)}}: array of 2…8 points [[x, y], …] with x, y in 0…1, x increasing,
              including endpoints (identity is [[0,0],[1,1]]). E.g. faded blacks: CurveMaster [[0,0.06],[0.25,0.24],[0.75,0.78],[1,1]].

            LOCAL MASKS ("masks" array, 0…{{maxMasks}} per look, only when they clearly help):
            - "kind": one of "linear", "radial"{{string.Concat(aiMaskKinds.Select(k => $", \"{KindName(k)}\""))}}.
              AI kinds are segmented automatically by Lumos (sky, subject = one object you point at, water, vegetation, mountains, architecture = buildings,
              natural_ground = earth/sand/field, artificial_ground = roads/floors). Only use AI kinds that are actually present in the photo.
            - Coordinates are fractions 0…1 of the attached image (x from left, y from top).
              linear: full effect at (x1, y1), fading to zero at (x2, y2) — e.g. graduated sky: x1 0.5, y1 0, x2 0.5, y2 0.45.
              radial: ellipse centered at (x1, y1), radius_x/radius_y as fractions of width/height, angle in degrees, feather 0…100.
              subject: ONE concrete object you point at — its rough bounding box as center (x1, y1) and half width/height
              (radius_x, radius_y), e.g. a small car in the distance: x1 0.42, y1 0.54, radius_x 0.05, radius_y 0.025. Lumos outlines
              exactly that object (not the whole box). Without radius_x/radius_y it takes the most prominent object of the frame.
              Prefer "subject" over a radial whenever the adjustment is about the object itself.
            - "invert": true to affect everything OUTSIDE the mask.
            - "adjustments": the SAME parameter names as global settings but RELATIVE to the global result
              (0 = no extra change; e.g. Exposure −0.4 darkens the masked area by 0.4 EV). Curves, HSL and grading are allowed too.
            - "name": short Ukrainian name of the mask.

            """);
    }

    // ── «✨ AI Pro»: спершу план, як у розборі професіонала, потім варіанти за ним ──────────────────

    /// <summary>Інструкція для першого кроку Pro: розібрати кадр і скласти план обробки.</summary>
    public static string PlanInstruction(int count) => $$"""
        You are a senior landscape, travel and portrait photo editor — the kind who posts annotated before/after breakdowns
        ("mask the sky and bring it back", "slight dehaze on the mountains", "brush the boat to draw attention", "radial filter
        in the middle, brighten a little", "darken the foreground a little", "push cool tones into the whole photo", "remove").
        You get the as-shot photo, a REGION MAP from Lumos's segmentation networks (colored, labeled overlay + exact
        measurements), and a −1.5 EV preview showing what the RAW holds in the highlights.
        Write the EDIT PLAN for this photo, the way you would annotate it:
        - "story": what the photo is about and the mood it can have; "eye": where the eye must go and what pulls it away now.
        - "global": 3–6 global steps (white balance and color temperature, overall exposure/tone, the color palette with HSL —
          which colors to shift, calm or enrich — contrast/curve, grading). Concrete, for THIS photo.
        - "local": 4–8 local steps, each on ONE target: a region key from the map (sky, mountains, water, vegetation, subject, …)
          or "radial" (a pool of light, brightening the center/subject area, calmer edges) or "linear" (darken the foreground,
          the top or a side). Typical pro moves: bring back a blown sky that the RAW still holds; dehaze distant mountains a
          little; make the subject pop (brighter, a bit more color and clarity); a soft radial lift where light falls; darken the
          foreground a little so the eye travels in; calm a bright distracting area. Every step has a short Ukrainian "name",
          a precise English "goal" and "strength" (subtle | medium | strong). Only steps that really help this photo.
        - "remove": distractions to erase with AI removal — small things that pull the eye (a rock or branch poking in at an
          edge, litter, a sign, a stray tiny person, a sensor spot). Each smaller than ~5% of the frame, never part of the subject
          or of the main scenery; at most 3; empty if none. Give the Ukrainian "name" and the box: center x1, y1 and half sizes
          radius_x, radius_y (fractions of the image), slightly larger than the object.
        - "moods": {{count}} clearly different moods this plan can be finished in (e.g. cool moody Nordic, warm late light, soft film).
        Respect the user's wishes if given. Answer with JSON only:
        {"story": "...", "eye": "...", "global": ["..."], "local": [{"name": "...", "target": "sky", "goal": "...", "strength": "medium"}],
         "remove": [{"name": "...", "x1": 0.04, "y1": 0.72, "radius_x": 0.04, "radius_y": 0.05}], "moods": ["..."]}
        """;

    /// <summary>Запит для плану (і для варіантів — разом із самим планом): дані фото, ділянки, фото, карта, «−1,5 EV».</summary>
    public static List<GeminiPart> ProParts(Photo photo, DevelopSettings current, string? wish, byte[] original, RegionMap map, string? plan)
    {
        var parts = new List<GeminiPart>
        {
            new(UserPrompt(photo, current, wish) + map.Describe() +
                (plan is null ? "" : "\nEDIT PLAN for this photo (implement all of it in every look): " + plan) + "\nThe photo as shot:"),
            new(null, original),
            new("REGION MAP (Lumos's segmentation; colored and labeled with the region keys):"),
            new(null, map.Overlay),
            new(Inv($"The same frame at {RegionMap.RecoveryEv:0.0} EV — what the file holds in the highlights:")),
            new(null, map.Recovery),
        };
        return parts;
    }

    /// <summary>План обробки: сам JSON (для другого кроку й перевірки), кроки українською для показу і що прибрати.</summary>
    public sealed record AiPlan(string Json, string Story, IReadOnlyList<string> Steps, IReadOnlyList<AiRemoval> Removals);

    /// <summary>Що прибрати ШІ-видаленням: назва й рамка (центр і піврозміри, частки кадру).</summary>
    public sealed record AiRemoval(string Name, double X, double Y, double HalfW, double HalfH);

    public static AiPlan ParsePlan(string answer)
    {
        var root = ParseRoot(answer);
        var steps = new List<string>();
        if (root["local"] is JsonArray local)
            foreach (var step in local.OfType<JsonObject>())
                steps.Add(Text(step["name"], Text(step["goal"], "")));
        var removals = new List<AiRemoval>();
        if (root["remove"] is JsonArray remove)
            foreach (var r in remove.OfType<JsonObject>().Take(3))
            {
                double hw = Num(r["radius_x"], 0, 0, 0.5), hh = Num(r["radius_y"], 0, 0, 0.5);
                // Великі «відволікання» — радше помилка моделі: ШІ-видалення розмаже пів кадру.
                if (hw <= 0.003 || hh <= 0.003 || hw * hh * 4 > 0.06) continue;
                removals.Add(new AiRemoval(Text(r["name"], "предмет"), Num(r["x1"], 0.5, 0, 1), Num(r["y1"], 0.5, 0, 1), hw, hh));
            }
        return new AiPlan(root.ToJsonString(), Text(root["story"], ""), steps.Where(s => s.Length > 0).ToList(), removals);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Текст запиту: дані про фото, поточна обрізка і побажання людини.</summary>
    public static string UserPrompt(Photo photo, DevelopSettings current, string? wish)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Photo: {(photo.Kind == PhotoKind.Raw ? "RAW" : "JPEG/rendered")}, {photo.FullWidth}×{photo.FullHeight}.");
        if (!string.IsNullOrWhiteSpace(photo.Camera)) sb.AppendLine($"Camera: {photo.Camera}. Lens: {photo.Lens}. Exposure: {photo.Exposure}.");
        if (!ImageGeometry.IsFullCrop(current))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"The user crops the final image to x {current.CropX:0.###}…{current.CropX + current.CropW:0.###}, y {current.CropY:0.###}…{current.CropY + current.CropH:0.###} of the attached image; judge the look for that crop."));
        sb.AppendLine(string.IsNullOrWhiteSpace(wish) ? "The user has no specific wishes — suggest what suits the photo best." : $"User's wishes: {wish.Trim()}");
        return sb.ToString();
    }

    internal static string KindName(MaskKind kind) => MaskKinds.First(p => p.Value == kind).Key;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>
    /// Друге коло: модель бачить оригінал і те, що наш рушій реально зробив з кожного її варіанта, — і виправляє числа
    /// (сильніше, де ефект не читається; м'якше, де перебір; маски — туди, куди треба). Без цього вона вгадує наосліп.
    /// </summary>
    /// <param name="originalMetrics">Вимірювання оригіналу — від них рахуються огріхи варіантів (<see cref="LookMetrics.Problems"/>).</param>
    /// <param name="pro">«✨ AI Pro»: карта ділянок, вимірювання кожного варіанта по ділянках (у тому ж порядку) і план обробки.</param>
    public static List<GeminiPart> RefineParts(byte[] original, LookMetrics originalMetrics,
        IReadOnlyList<(AiSuggestion Look, byte[] Render, LookMetrics Metrics)> looks, string? wish,
        (RegionMap Map, IReadOnlyList<RegionReport> Reports, string Plan)? pro = null)
    {
        var parts = new List<GeminiPart>
        {
            new($"""
                Here is the original photo (as shot), followed by the ACTUAL renders that Lumos produced from your {looks.Count} looks,
                each with its intent, the exact JSON you gave and objective measurements of the render (CIELAB). Mask coordinates
                refer to these full, uncropped images.
                {(string.IsNullOrWhiteSpace(wish) ? "" : "User's wishes: " + wish.Trim())}
                Original — measured: {originalMetrics.Describe()}
                {(pro is { } p ? p.Map.Describe() + "\nEDIT PLAN (every look must still implement all of it): " + p.Plan : "")}
                """),
            new(null, original),
        };
        for (int i = 0; i < looks.Count; i++)
        {
            var (look, render, metrics) = looks[i];
            var problems = metrics.Problems(originalMetrics);
            string regions = "";
            if (pro is { } r && i < r.Reports.Count)
            {
                problems.AddRange(r.Map.Problems(r.Reports[i]));
                regions = r.Map.Describe(r.Reports[i]);
            }
            parts.Add(new($"""
                Look {i + 1}: «{look.Name}». JSON: {look.Raw.ToJsonString()}
                Measured: {metrics.Describe()}
                {regions}
                {(problems.Count == 0 ? "Measured problems: none." : "MEASURED PROBLEMS (must fix):\n- " + string.Join("\n- ", problems))}
                Render of look {i + 1}:
                """));
            parts.Add(new(null, render));
        }
        parts.Add(new("""
            Act as an experienced photo editor reviewing these renders with fresh eyes. The measurements are reliable — trust them
            over your first impression: fix every MEASURED PROBLEM (a muddy third, crushed blacks, a violet cast on gray roads,
            clouds or mountains) unless it is unmistakably the purpose of that look. For EACH look also check:
            - Does it look like a natural, well-made photograph? Or does it look over-processed: too dark or muddy, crushed blacks,
              flat gray highlights, halos, HDR look, unnatural hues (cyan greens, orange skin), oversaturation? Fix these first.
            - Is it truly atmospheric — does it convey its mood at thumbnail size? If it reads as a plain correction, build the mood
              (light shaping with soft masks, depth, color mood, curve) without over-processing.
            - Does it deliver its intent and suit this photo?
            - Are the masks on the right areas and soft enough? Move, soften or remove them if not.
            - Do the looks have clearly different moods (without forcing black-and-white)? If two feel alike, change one.
            Then return ALL looks revised, in the same JSON format and order, with FULL settings (not a diff) and corrected numbers.
            Pull back what is overdone; strengthen only what is truly invisible; replace a look entirely if it does not suit the photo.
            For each look add "critique": one Ukrainian sentence saying what you changed and why.
            """));
        return parts;
    }

    /// <summary>Запит до Gemini (generateContent): інструкція, текст і фото; відповідь — JSON-текст моделі.</summary>
    public static Task<string> AskGeminiAsync(string apiKey, string model, string system, string prompt, byte[] jpeg, CancellationToken cancel) =>
        AskGeminiAsync(apiKey, model, system, [new GeminiPart(prompt), new GeminiPart(null, jpeg)], cancel);

    public static async Task<string> AskGeminiAsync(string apiKey, string model, string system, IReadOnlyList<GeminiPart> content, CancellationToken cancel)
    {
        var parts = new JsonArray();
        foreach (var part in content)
            parts.Add(part.Jpeg is { } jpeg
                ? new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpeg) } }
                : new JsonObject { ["text"] = part.Text ?? "" });
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generation_config"] = new JsonObject { ["response_mime_type"] = "application/json" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (JsonException) { throw new InvalidOperationException($"Gemini відповів не JSON (HTTP {(int)response.StatusCode})."); }

        if (!response.IsSuccessStatusCode)
        {
            string message = json?["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase ?? "";
            throw new InvalidOperationException($"Gemini: {message} (HTTP {(int)response.StatusCode})");
        }
        var answerParts = json?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        string answer = string.Concat(answerParts?.Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>() ?? "") ?? []);
        if (answer.Length == 0)
        {
            string reason = json?["promptFeedback"]?["blockReason"]?.GetValue<string>()
                            ?? json?["candidates"]?[0]?["finishReason"]?.GetValue<string>() ?? "порожня відповідь";
            throw new InvalidOperationException($"Gemini не дав варіантів ({reason}).");
        }
        return answer;
    }

    /// <summary>
    /// Розбір відповіді в правки. Базою є стартові правки фото (для RAW — різкість і шумозаглушення) з поточною геометрією й латками:
    /// стиль замінює лише вигляд, а не обрізку чи прибрані плями.
    /// </summary>
    public static List<AiSuggestion> Parse(string answer, DevelopSettings current, string photoPath, int maxMasks = MaxMasks)
    {
        // Модель інколи загортає JSON у ```json … ``` попри інструкцію.
        var root = ParseRoot(answer);
        var looks = (root["looks"] ?? root["styles"]) as JsonArray ?? throw new InvalidOperationException("У відповіді Gemini немає варіантів.");

        var result = looks.OfType<JsonObject>().Select(look => ParseLook(look, current, photoPath, maxMasks)).ToList();
        if (result.Count == 0) throw new InvalidOperationException("Gemini не запропонував жодного варіанта.");
        return result;
    }

    /// <summary>JSON-відповідь моделі (без можливої обгортки ```json) як об'єкт.</summary>
    internal static JsonObject ParseRoot(string answer)
    {
        string trimmed = answer.Trim();
        int start = trimmed.IndexOf('{'), end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Gemini відповів не у форматі JSON.");
        return JsonNode.Parse(trimmed[start..(end + 1)]) as JsonObject ?? throw new InvalidOperationException("Порожня відповідь Gemini.");
    }

    /// <summary>Один варіант для конкретного фото: його стартові правки, геометрія й латки + числа моделі.</summary>
    internal static AiSuggestion ParseLook(JsonObject look, DevelopSettings current, string photoPath, int maxMasks = MaxMasks)
    {
        var settings = DevelopSettings.DefaultFor(photoPath);
        settings.CopyGeometryFrom(current);
        (settings.CropX, settings.CropY, settings.CropW, settings.CropH) = (current.CropX, current.CropY, current.CropW, current.CropH);
        settings.Heals = current.Heals;
        // Розмиття об'єктива — як геометрія: властивість кадру, стиль його не чіпає.
        (settings.BlurAmount, settings.BlurRange, settings.BlurBokeh) = (current.BlurAmount, current.BlurRange, current.BlurBokeh);
        (settings.BlurFocusX, settings.BlurFocusY, settings.DepthMap) = (current.BlurFocusX, current.BlurFocusY, current.DepthMap);
        if (look["settings"] is JsonObject values) ApplyValues(settings, values, relative: false);

        var masks = new List<AiMaskSuggestion>();
        if (look["masks"] is JsonArray maskArray)
            foreach (var m in maskArray.OfType<JsonObject>().Take(maxMasks))
                if (ParseMask(m) is { } mask) masks.Add(mask);

        return new AiSuggestion(Text(look["name"], "Варіант"), Text(look["description"], ""), settings, masks)
        {
            Raw = (JsonObject)look.DeepClone(),
            Critique = look["critique"] is { } critique ? Text(critique, "") is { Length: > 0 } c ? c : null : null,
        };
    }

    /// <summary>Число з JSON у межах (для поправок серії); null — немає чи не число.</summary>
    internal static double? Number(JsonObject o, string name, double min, double max) =>
        Find(o, name) is { } node && TryNum(node, out double v) ? Math.Clamp(v, min, max) : null;

    internal static string TextOf(JsonNode? node, string fallback) => Text(node, fallback);

    private static AiMaskSuggestion? ParseMask(JsonObject m)
    {
        if (!MaskKinds.TryGetValue(Text(m["kind"], ""), out var kind)) return null;
        double x1 = Num(m["x1"], 0.5, 0, 1), y1 = Num(m["y1"], 0.5, 0, 1);
        var shape = kind switch
        {
            MaskKind.Linear => new Mask { Kind = kind, X1 = x1, Y1 = y1, X2 = Num(m["x2"], 0.5, 0, 1), Y2 = Num(m["y2"], 0.5, 0, 1) },
            MaskKind.Radial => new Mask
            {
                Kind = kind, X1 = x1, Y1 = y1, RadiusX = Num(m["radius_x"], 0.25, 0.02, 1.5), RadiusY = Num(m["radius_y"], 0.25, 0.02, 1.5),
                Angle = Num(m["angle"], 0, -180, 180), Feather = Num(m["feather"], 50, 0, 100),
            },
            // Рамка об'єкта (центр + піврозміри) — Lumos обведе саме його; без рамки — головний об'єкт кадру (RadiusX 0).
            MaskKind.Subject when m["radius_x"] is not null || m["radius_y"] is not null => new Mask
            {
                Kind = kind, X1 = x1, Y1 = y1,
                RadiusX = Num(m["radius_x"] ?? m["radius_y"], 0.1, 0.01, 0.5), RadiusY = Num(m["radius_y"] ?? m["radius_x"], 0.1, 0.01, 0.5),
            },
            MaskKind.Subject => new Mask { Kind = kind, RadiusX = 0, RadiusY = 0 },
            _ => new Mask { Kind = kind },
        };
        var local = new DevelopSettings();
        if (m["adjustments"] is JsonObject adjustments) ApplyValues(local, adjustments, relative: true);
        string fallback = AiMaskTypes.Of(kind)?.Name ?? (kind == MaskKind.Linear ? "Лінійна" : "Радіальна");
        return new AiMaskSuggestion(Text(m["name"], fallback), kind, m["invert"]?.GetValueKind() == JsonValueKind.True, shape, local);
    }

    /// <summary>Переносить відомі параметри з JSON у правки з обрізанням до меж; невідоме мовчки пропускається.</summary>
    private static void ApplyValues(DevelopSettings s, JsonObject values, bool relative)
    {
        // Відносні правки маски для полів із ненульовим «нейтральним» значенням (напр. розмиття віньєтки 50) — теж від нуля.
        var props = typeof(DevelopSettings).GetProperties().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var p in Scalars)
        {
            if (Find(values, p.Name) is not { } node || !TryNum(node, out double v)) continue;
            var prop = props[p.Name];
            double neutral = (double)prop.GetValue(new DevelopSettings())!;
            double value = relative ? neutral + Math.Clamp(v, p.Min - p.Max, p.Max - p.Min) : Math.Clamp(v, p.Min, p.Max);
            prop.SetValue(s, value);
        }
        foreach (string band in HslBands)
            if (Find(values, "Hsl" + band) is JsonObject o)
                props["Hsl" + band].SetValue(s, new HslAdjust(Num(o["Hue"] ?? o["hue"], 0, -100, 100),
                    Num(o["Saturation"] ?? o["saturation"], 0, -100, 100), Num(o["Luminance"] ?? o["luminance"], 0, -100, 100)));
        foreach (string wheel in Wheels)
            if (Find(values, "Grade" + wheel) is JsonObject o)
            {
                double hue = Num(o["Hue"] ?? o["hue"], 0, -720, 720);
                props["Grade" + wheel].SetValue(s, new GradeWheel(((hue % 360) + 360) % 360,
                    Num(o["Saturation"] ?? o["saturation"], 0, 0, 100), Num(o["Luminance"] ?? o["luminance"], 0, -100, 100)));
            }
        foreach (string channel in Curves)
            if (Find(values, "Curve" + channel) is JsonArray points && ParseCurve(points) is { } curve)
                props["Curve" + channel].SetValue(s, curve);
    }

    private static Curve? ParseCurve(JsonArray points)
    {
        var list = new List<CurvePoint>();
        foreach (var p in points)
        {
            JsonNode? xn = null, yn = null;
            if (p is JsonArray { Count: >= 2 } pair) (xn, yn) = (pair[0], pair[1]);
            else if (p is JsonObject o) (xn, yn) = (o["x"] ?? o["X"], o["y"] ?? o["Y"]);
            if (xn is null || yn is null || !TryNum(xn, out double x) || !TryNum(yn, out double y)) continue;
            // Хтось пише криві в 0…255, як у Lightroom.
            if (x > 1.5 || y > 1.5) (x, y) = (x / 255, y / 255);
            list.Add(new CurvePoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
        }
        if (list.Count < 2) return null;
        var unique = list.GroupBy(p => Math.Round(p.X, 3)).Select(g => g.First()).OrderBy(p => p.X).Take(12).ToList();
        return unique.Count >= 2 ? new Curve(unique) : null;
    }

    private static JsonNode? Find(JsonObject o, string name) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool TryNum(JsonNode node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out double d)) value = d;
        else if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) value = d;
        else return false;
        return double.IsFinite(value);
    }

    private static double Num(JsonNode? node, double fallback, double min, double max) =>
        node is not null && TryNum(node, out double v) ? Math.Clamp(v, min, max) : fallback;

    private static string Text(JsonNode? node, string fallback) =>
        node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : fallback;
}
