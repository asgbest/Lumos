using Sdcb.LibRaw;

namespace Lumos.Core;

/// <summary>
/// Відкриває RAW через LibRaw: демозаїка, баланс білого камери, матриця камери → лінійний sRGB, 16 біт.
/// Без гамми й автояскравості — усю тонову обробку робить <see cref="Developer"/>.
/// </summary>
public static class RawDecoder
{
    /// <summary>Розширення, які пропонує вікно відкриття. LibRaw читає й інші — перевіряється вмістом, а не назвою.</summary>
    public static readonly string[] Extensions =
    [
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "orf", "rw2", "pef",
        "dng", "srw", "x3f", "3fr", "fff", "iiq", "mos", "mef", "mrw", "erf", "kdc", "dcr", "rwl",
    ];

    public static Photo Load(string path, bool halfSize = false)
    {
        using RawContext raw = RawContext.OpenFile(path);
        raw.Unpack();
        raw.DcrawProcess(o =>
        {
            o.HalfSize = halfSize; // без демозаїки: кожен квадрат 2×2 сенсора — один піксель
            o.UseCameraWb = true;
            o.NoAutoBright = true;
            o.OutputBps = 16;
            o.Gamma[0] = 1; // лінійний вихід
            o.Gamma[1] = 1;
            o.OutputColor = LibRawColorSpace.SRGB;
            o.HighlightMode = 2; // м'яке відновлення пересвічених ділянок замість різкого обрізання
            o.UserQual = DemosaicAlgorithm.AdaptiveHomogeneityDirected; // демозаїка AHD
        });

        using ProcessedImage processed = raw.MakeDcrawMemoryImage();
        int w = processed.Width, h = processed.Height;
        ReadOnlySpan<ushort> src = processed.AsSpan<ushort>();
        var pixels = new float[w * h * 3];
        const float scale = 1f / 65535f;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = src[i] * scale;

        var p = raw.ImageParams;
        var other = raw.ImageOtherParams;
        string camera = $"{p.Make} {p.Model}".Trim();
        string lens = raw.LensInfo.Lens?.Trim() ?? "";
        string shutter = other.Shutter is > 0 and < 1 ? $"1/{1 / other.Shutter:0}" : $"{other.Shutter:0.#}";
        string exposure = $"ISO {other.IsoSpeed:0} · {shutter} с · f/{other.Aperture:0.#} · {other.FocalLength:0} мм";

        int factor = halfSize ? 2 : 1;
        // LibRaw повертає час зйомки як мітку Unix, порахувавши її від місцевого часу камери, — назад переводимо так само.
        DateTime? taken = other.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(other.Timestamp).LocalDateTime : null;
        return new Photo(path, new LinearImage(w, h, pixels), PhotoKind.Raw, camera, lens, exposure, w * factor, h * factor)
        {
            Exif = ExifMetadata.FromRaw(p.Make?.Trim() ?? "", p.Model?.Trim() ?? "", lens, other.IsoSpeed, other.Shutter,
                other.Aperture, other.FocalLength, taken, other.Artist?.Trim() ?? ""),
        };
    }

    /// <summary>
    /// Вбудований у RAW JPEG (камера кладе його для перегляду на екрані): готовий за мілісекунди,
    /// тож ним показуємо мініатюри й саме фото, поки справжнє розкодовується. null — якщо його немає.
    /// </summary>
    public static (byte[] Jpeg, int Rotation)? EmbeddedPreview(string path)
    {
        try
        {
            using RawContext raw = RawContext.OpenFile(path);
            using ProcessedImage thumb = raw.ExportThumbnail(0);
            if (thumb.ImageType != ProcessedImageType.Jpeg) return null;
            // Поворот кадру з RAW (вбудований JPEG зазвичай не повернутий): 3 — 180°, 5 — 90° проти годинникової, 6 — 90° за.
            int rotation = Flip(raw) switch { 3 => 180, 5 => 270, 6 => 90, _ => 0 };
            return (thumb.AsSpan<byte>().ToArray(), rotation);
        }
        catch (LibRawException)
        {
            return null;
        }
    }

    // Обгортка Sdcb.LibRaw не відкриває поле повороту (sizes.flip) напряму — читаємо внутрішню структуру.
    private static readonly System.Reflection.PropertyInfo? RawDataProperty =
        typeof(RawContext).GetProperty("RawData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    private static int Flip(RawContext raw) =>
        RawDataProperty?.GetValue(raw) is Sdcb.LibRaw.Natives.LibRawData data ? data.ImageSizes.Flip : 0;
}
