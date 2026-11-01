namespace Lumos.Core;

/// <summary>Звідки фото: RAW — «сирі» дані сенсора; Rendered — уже готове зображення (JPEG, PNG, TIFF, HEIC…).</summary>
public enum PhotoKind { Raw, Rendered }

/// <summary>Розкодоване фото: лінійні пікселі в повній роздільності та короткі дані зйомки.</summary>
/// <remarks><see cref="FullWidth"/>/<see cref="FullHeight"/> — розмір оригіналу (якщо <see cref="Image"/> зменшене для прев'ю).</remarks>
public sealed record Photo(string Path, LinearImage Image, PhotoKind Kind, string Camera, string Lens, string Exposure,
    int FullWidth, int FullHeight)
{
    /// <summary>Дані зйомки для експорту (EXIF).</summary>
    public ImageMagick.IExifProfile? Exif { get; init; }

    /// <summary>RAW потребує базового профілю (яскравість, контраст, м'яке «плече»); готове фото — ні, воно вже оброблене.</summary>
    public ToneProfile Profile => Kind == PhotoKind.Raw ? ToneProfile.Raw : ToneProfile.Rendered;
}

/// <summary>Відкриває будь-яке підтримуване фото: звичайні формати — через Magick.NET, решту — як RAW через LibRaw.</summary>
public static class PhotoDecoder
{
    public static readonly string[] RenderedExtensions = ["jpg", "jpeg", "png", "tif", "tiff", "webp", "heic", "heif", "bmp"];

    public static bool IsRendered(string path) =>
        RenderedExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    /// <param name="forPreview">RAW розкодовується в половинній роздільності (удвічі швидше) — для прев'ю цього досить;
    /// повна роздільність потрібна лише для масштабу 100 % і експорту.</param>
    public static Photo Load(string path, bool forPreview = false) =>
        IsRendered(path) ? RenderedDecoder.Load(path) : RawDecoder.Load(path, forPreview);

    public static bool IsSupported(string path)
    {
        string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return RenderedExtensions.Contains(ext) || RawDecoder.Extensions.Contains(ext);
    }
}
