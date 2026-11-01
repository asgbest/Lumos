using ImageMagick;

namespace Lumos.Core;

/// <summary>
/// Відкриває готові фото (JPEG, PNG, TIFF, WebP, HEIC) через Magick.NET і переводить їх у той самий лінійний RGB, що й RAW:
/// поворот за EXIF → вбудований колірний профіль (напр. Display P3 з iPhone) → sRGB → лінійне світло.
/// 16-бітні TIFF/PNG зберігають свою точність.
/// </summary>
public static class RenderedDecoder
{
    /// <summary>sRGB (0…65535) → лінійне світло (0…1).</summary>
    private static readonly float[] SrgbToLinear = BuildSrgbToLinear();

    public static Photo Load(string path)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        // Вбудований профіль → sRGB; якщо профілю немає, вважаємо, що фото вже в sRGB, і нічого не змінюється.
        image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.SRGB);
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray))
            image.ColorSpace = ColorSpace.sRGB; // CMYK тощо без профілю

        int w = (int)image.Width, h = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        ushort[] rgb = pixels.ToShortArray(PixelMapping.RGB)
            ?? throw new InvalidOperationException("Не вдалося прочитати пікселі.");

        var linear = new float[w * h * 3];
        var lut = SrgbToLinear;
        Parallel.For(0, h, y =>
        {
            int start = y * w * 3, end = start + w * 3;
            for (int i = start; i < end; i++) linear[i] = lut[rgb[i]];
        });

        var (camera, lens, exposure) = ReadExif(image);
        return new Photo(path, new LinearImage(w, h, linear), PhotoKind.Rendered, camera, lens, exposure, w, h)
        {
            Exif = image.GetExifProfile(),
        };
    }

    private static (string Camera, string Lens, string Exposure) ReadExif(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif is null) return (image.Format.ToString().ToUpperInvariant(), "", "");

        string camera = $"{exif.GetValue(ExifTag.Make)?.Value} {exif.GetValue(ExifTag.Model)?.Value}".Trim();
        string lens = exif.GetValue(ExifTag.LensModel)?.Value?.Trim() ?? "";

        var parts = new List<string>();
        if (exif.GetValue(ExifTag.ISOSpeedRatings)?.Value is { Length: > 0 } iso) parts.Add($"ISO {iso[0]}");
        if (exif.GetValue(ExifTag.ExposureTime)?.Value is { Denominator: > 0 } t)
        {
            double s = t.ToDouble();
            parts.Add(s is > 0 and < 1 ? $"1/{1 / s:0} с" : $"{s:0.#} с");
        }
        if (exif.GetValue(ExifTag.FNumber)?.Value is { Denominator: > 0 } f) parts.Add($"f/{f.ToDouble():0.#}");
        if (exif.GetValue(ExifTag.FocalLength)?.Value is { Denominator: > 0 } fl) parts.Add($"{fl.ToDouble():0} мм");

        return (camera.Length > 0 ? camera : image.Format.ToString().ToUpperInvariant(), lens, string.Join(" · ", parts));
    }

    private static float[] BuildSrgbToLinear()
    {
        var lut = new float[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            double v = i / 65535.0;
            lut[i] = (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
        }
        return lut;
    }
}
