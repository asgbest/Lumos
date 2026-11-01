using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ImageMagick;

namespace Lumos.Core;

/// <summary>Кадр серії: файл, його прев'ю й поточні правки (геометрія, обрізка й латки лишаються його власні).</summary>
public sealed record SeriesPhoto(string Path, Photo Photo, DevelopSettings Current);

/// <summary>
/// Стиль для серії від Gemini: спільний вигляд (як у <see cref="AiSuggestion"/>, сирий JSON) і поправки для окремих кадрів
/// (ключ — номер кадру серед показаних моделі, з 0), щоб серія виглядала однаково при різному світлі й експозиції.
/// </summary>
public sealed record SeriesLook(string Name, string Description, string? Critique, JsonObject Raw, IReadOnlyDictionary<int, JsonObject> Offsets);

/// <summary>
/// «✨ AI для серії»: Gemini бачить усі вибрані кадри разом і пропонує кілька спільних стилів — один вигляд на всю серію
/// плюс невеликі поправки кожному кадру (експозиція, баланс білого, тон), щоб вони стали однорідними.
/// Друге коло — модель дивиться на «контактні аркуші»: усі кадри з кожним стилем, як їх реально зробив наш рушій.
/// </summary>
public static class AiSeries
{
    public const int MaxPhotos = 20;
    public const int ImageSide = 640;
    public const int SheetCell = 300;

    /// <summary>Що можна підправити окремому кадру і в яких межах (відносно спільного стилю).</summary>
    private static readonly (string Name, double Limit, double Min, double Max)[] OffsetParams =
    [
        ("Exposure", 2, -5, 5), ("Temperature", 40, -100, 100), ("Tint", 30, -100, 100), ("Contrast", 30, -100, 100),
        ("Highlights", 50, -100, 100), ("Shadows", 50, -100, 100), ("Whites", 40, -100, 100), ("Blacks", 40, -100, 100),
    ];

    /// <summary>
    /// Вирівнювання яскравості серії ще до Gemini (моделі погано вгадують точну різницю в ступенях): для кожного кадру —
    /// медіана логарифма яскравості (без найсвітліших 3 %: вікно чи лампа в кадрі не мають тягнути все вниз),
    /// і поправка експозиції до медіани серії на 85 % різниці (до ±4 EV) — щоб навмисно темний кадр лишився трохи темнішим.
    /// </summary>
    public static double[] ExposureBaseline(IReadOnlyList<SeriesPhoto> photos)
    {
        var levels = photos.Select(p =>
        {
            var image = p.Photo.Image.Downscale(256);
            var logs = new List<double>(image.Width * image.Height);
            var px = image.Pixels;
            for (int i = 0; i < px.Length; i += 3)
                logs.Add(Math.Log2(Math.Max(1e-5, 0.2126 * px[i] + 0.7152 * px[i + 1] + 0.0722 * px[i + 2])));
            logs.Sort();
            return logs[(int)(logs.Count * 0.97 / 2)]; // медіана серед усього, крім найсвітліших 3 %
        }).ToArray();
        double median = levels.OrderBy(v => v).ElementAt(levels.Length / 2);
        return levels.Select(l => Math.Round(Math.Clamp((median - l) * 0.85, -4, 4) * 20) / 20).ToArray();
    }

    /// <summary>Які кадри показати моделі: усі, а якщо їх більше за <see cref="MaxPhotos"/> — рівномірно через серію.</summary>
    public static int[] Sample(int count) =>
        count <= MaxPhotos
            ? Enumerable.Range(0, count).ToArray()
            : Enumerable.Range(0, MaxPhotos).Select(i => (int)Math.Round(i * (count - 1) / (double)(MaxPhotos - 1))).Distinct().ToArray();

    /// <summary>Номер показаного кадру, найближчого до кадру <paramref name="index"/> (для тих, кого модель не бачила).</summary>
    public static int Nearest(int[] sample, int index) =>
        Enumerable.Range(0, sample.Length).MinBy(i => Math.Abs(sample[i] - index));

    public static string SystemInstruction(IEnumerable<MaskKind> aiMaskKinds, int looks, int photos)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            You are an award-winning photo editor and film colorist working inside Lumos, a Lightroom-like RAW editor.
            The user attaches a SERIES of {{photos}} photos from one shoot (numbered 1…{{photos}}), each rendered with neutral default
            processing ("as shot"), except that Lumos has ALREADY matched their overall brightness automatically (the per-photo
            exposure it added is written next to each photo; it stays applied under your look). Your job is to give the whole series ONE coherent, atmospheric look — like a finished set in a
            magazine or a photo book — and design {{looks}} such looks with clearly different moods.

            First really LOOK at all the photos together: what the shoot is about, the light (time of day, weather, indoor/outdoor),
            the dominant colors, and how the frames differ (some darker or brighter, warmer or cooler, different light).

            What makes a good answer:
            - A look is one shared recipe ("settings" + "masks") applied to every photo. It must suit ALL frames, not just one.
            - Every look must be ATMOSPHERIC (a mood, a feeling of place and time), believable and well-made: natural light,
              clean skin and skies, detail in highlights and shadows. A plain "natural correction" is a failure; so is over-processing.
            - The looks must have clearly DIFFERENT moods, distinguishable at thumbnail size. Black-and-white only if the series is
              clearly stronger in monochrome, and then at most one.
            - Measured values: most edits live within ±40. Avoid heavy hue shifts of natural colors (greens, skin, sky).
            - CONSISTENCY is the point of a series: for every photo whose exposure, white balance or contrast still differs from the
              rest (as you see it in the attached, already brightness-matched images), give a small per-photo correction so that,
              after the shared look, all frames match in brightness and color. Usually Exposure corrections stay within ±0.5.
              Per-photo corrections are RELATIVE to the shared look and only these keys are allowed:
              {{string.Join(", ", OffsetParams.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name} (±{p.Limit})")))}}.
              Omit photos that need nothing. Keep the photo's intended mood — do not flatten a deliberate night shot into daylight.
            - Masks are applied identically to every photo: prefer AI masks (they find the sky/subject/… in each frame by themselves);
              use linear/radial masks only if they make sense for every frame (e.g. a soft centered glow, a darker top edge).
            Respect the user's wishes if given — they override these defaults.

            """);
        AiStylist.AppendParameters(sb, aiMaskKinds);
        sb.AppendLine("""
            Answer with JSON only, no markdown:
            {"looks": [{"name": "...", "intent": "...", "description": "...", "settings": {...}, "masks": [...],
              "per_photo": [{"photo": 2, "Exposure": 0.3, "Temperature": -8}, ...] } ] }
            "photo" is the 1-based number of the photo in the series. Masks use the same format as described above.
            "intent" (English, 1–2 sentences): the precise visual goal of the look for this series — it is used later to check the result.
            "name" (2–4 words) and "description" (1–2 sentences: what the look does and why it suits this series) MUST be in Ukrainian.
            """);
        return sb.ToString();
    }

    /// <summary>Запит: кожен кадр із номером, даними зйомки й автоматичною поправкою яскравості, потім побажання.</summary>
    public static List<GeminiPart> Parts(IReadOnlyList<(SeriesPhoto Photo, byte[] Jpeg, double Baseline)> photos, string? wish)
    {
        var parts = new List<GeminiPart>();
        for (int i = 0; i < photos.Count; i++)
        {
            var p = photos[i].Photo.Photo;
            string shot = string.IsNullOrWhiteSpace(p.Camera) ? "" : $"{p.Exposure}; ";
            parts.Add(new(string.Create(CultureInfo.InvariantCulture, $"Photo {i + 1} ({shot}auto brightness match {photos[i].Baseline:+0.00;-0.00;0} EV):")));
            parts.Add(new(null, photos[i].Jpeg));
        }
        parts.Add(new(string.IsNullOrWhiteSpace(wish)
            ? "The user has no specific wishes — suggest what suits this series best."
            : $"User's wishes: {wish.Trim()}"));
        return parts;
    }

    public static List<SeriesLook> Parse(string answer, int photos)
    {
        var root = AiStylist.ParseRoot(answer);
        var looks = (root["looks"] ?? root["styles"]) as JsonArray ?? throw new InvalidOperationException("У відповіді Gemini немає варіантів.");
        var result = new List<SeriesLook>();
        foreach (var look in looks.OfType<JsonObject>())
        {
            var offsets = new Dictionary<int, JsonObject>();
            if (look["per_photo"] is JsonArray perPhoto)
                foreach (var entry in perPhoto.OfType<JsonObject>())
                    if (AiStylist.Number(entry, "photo", 1, photos) is { } n) offsets[(int)Math.Round(n) - 1] = entry;
            string? critique = look["critique"] is { } c && AiStylist.TextOf(c, "") is { Length: > 0 } text ? text : null;
            result.Add(new SeriesLook(AiStylist.TextOf(look["name"], "Варіант"), AiStylist.TextOf(look["description"], ""), critique,
                (JsonObject)look.DeepClone(), offsets));
        }
        if (result.Count == 0) throw new InvalidOperationException("Gemini не запропонував жодного варіанта.");
        return result;
    }

    /// <summary>
    /// Стиль для одного кадру: його власна база (геометрія, обрізка, латки) + спільний стиль + автоматичне вирівнювання
    /// яскравості (<paramref name="baseline"/>, EV) + поправка від моделі. <paramref name="shown"/> — номер серед показаних
    /// моделі кадрів, чию поправку брати.
    /// </summary>
    public static AiSuggestion ForPhoto(SeriesLook look, int shown, SeriesPhoto photo, double baseline)
    {
        var suggestion = AiStylist.ParseLook(look.Raw, photo.Current, photo.Path);
        var s = suggestion.Settings;
        s.Exposure = Math.Clamp(s.Exposure + baseline, -5, 5);
        if (look.Offsets.TryGetValue(shown, out var offset))
        {
            foreach (var (name, limit, min, max) in OffsetParams)
                if (AiStylist.Number(offset, name, -limit, limit) is { } delta)
                {
                    var prop = typeof(DevelopSettings).GetProperty(name)!;
                    prop.SetValue(s, Math.Clamp((double)prop.GetValue(s)! + delta, min, max));
                }
        }
        return suggestion;
    }

    /// <summary>Контактний аркуш: кадри сіткою по <paramref name="columns"/> у ряд, зліва направо й згори донизу.</summary>
    public static byte[] Sheet(IReadOnlyList<Thumb> thumbs, int columns)
    {
        int rows = (thumbs.Count + columns - 1) / columns;
        const int gap = 6;
        using var sheet = new MagickImage(MagickColors.Black, (uint)(columns * (SheetCell + gap) + gap), (uint)(rows * (SheetCell + gap) + gap));
        for (int i = 0; i < thumbs.Count; i++)
        {
            var t = thumbs[i];
            using var cell = new MagickImage(t.Bgra, new PixelReadSettings((uint)t.Width, (uint)t.Height, StorageType.Char, PixelMapping.BGRA));
            cell.Alpha(AlphaOption.Off);
            cell.Resize(new MagickGeometry(SheetCell, SheetCell));
            int x = gap + i % columns * (SheetCell + gap) + (SheetCell - (int)cell.Width) / 2;
            int y = gap + i / columns * (SheetCell + gap) + (SheetCell - (int)cell.Height) / 2;
            sheet.Composite(cell, x, y, CompositeOperator.Over);
        }
        sheet.Format = MagickFormat.Jpeg;
        sheet.Quality = 85;
        return sheet.ToByteArray();
    }

    public static int Columns(int count) => count <= 4 ? count : count <= 9 ? 3 : count <= 16 ? 4 : 5;

    /// <summary>Друге коло: аркуш «як є» і аркуш кожного стилю, як його зробив наш рушій, — модель виправляє числа.</summary>
    public static List<GeminiPart> RefineParts(byte[] original, IReadOnlyList<(SeriesLook Look, byte[] Sheet)> looks, int photos, string? wish)
    {
        int columns = Columns(photos);
        var parts = new List<GeminiPart>
        {
            new($"""
                Contact sheets of the same {photos} photos, {columns} per row, left to right and top to bottom (photo 1 is top-left).
                {(string.IsNullOrWhiteSpace(wish) ? "" : "User's wishes: " + wish.Trim())}
                As shot:
                """),
            new(null, original),
        };
        for (int i = 0; i < looks.Count; i++)
        {
            var (look, sheet) = looks[i];
            parts.Add(new($"Look {i + 1}: «{look.Name}». JSON: {look.Raw.ToJsonString()}\nACTUAL render of look {i + 1} on the whole series:"));
            parts.Add(new(null, sheet));
        }
        parts.Add(new("""
            Act as an experienced photo editor reviewing a finished series with fresh eyes. For EACH look check:
            - Do all frames now match (brightness, white balance, contrast) like one coherent set? Fix per_photo corrections where a
              frame still stands out; remove corrections that made a frame worse.
            - Does it look natural and well-made, or over-processed (muddy, crushed blacks, halos, unnatural hues, oversaturation)?
            - Is it truly atmospheric at thumbnail size, does it deliver its intent, and does it suit every frame?
            - Do the looks have clearly different moods? If two feel alike, change one.
            Then return ALL looks revised, in the same JSON format and order, with FULL settings, masks and per_photo (not a diff).
            For each look add "critique": one Ukrainian sentence saying what you changed and why.
            """));
        return parts;
    }
}
