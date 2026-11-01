using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace Lumos.Core;

/// <summary>Маленьке готове зображення (BGRA 8 біт) — мініатюра чи швидкий показ фото.</summary>
public sealed record Thumb(int Width, int Height, byte[] Bgra);

/// <summary>
/// Мініатюри для бібліотеки:
/// • швидка — вбудований у RAW JPEG або сам JPEG/PNG/HEIC, зменшений (без правок);
/// • з правками — фото, прораховане конвеєром Lumos, зберігається в кеші на диску.
/// </summary>
public static class Thumbnails
{
    /// <summary>Швидке зображення без правок, довша сторона ≤ <paramref name="size"/>. null — не вдалося.</summary>
    public static Thumb? Fast(string path, int size)
    {
        try
        {
            if (!PhotoDecoder.IsRendered(path))
            {
                if (RawDecoder.EmbeddedPreview(path) is not { } embedded) return null;
                using var image = Read(embedded.Jpeg, size);
                if (embedded.Rotation != 0) image.Rotate(embedded.Rotation);
                return ToThumb(image, size);
            }
            using var rendered = Read(path, size);
            rendered.AutoOrient();
            rendered.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.SRGB);
            return ToThumb(rendered, size);
        }
        catch (Exception ex) when (ex is MagickException or IOException)
        {
            return null;
        }
    }

    /// <summary>Мініатюра з правками: прев'ю фото → геометрія → увесь конвеєр, довша сторона ≤ <paramref name="size"/>.</summary>
    public static Thumb Edited(Photo photo, DevelopSettings s, int size)
    {
        var small = photo.Image.Downscale(size * 2);
        var image = ImageGeometry.Apply(small, s, crop: true);
        var bgra = new byte[image.Width * image.Height * 4];
        double scale = small.Width / (double)photo.FullWidth;
        PhotoRenderer.Render(image, s, photo.Profile, bgra, new int[Developer.HistogramBins * 3],
            RenderFrame.For(image, scale, Math.Max(photo.FullWidth, photo.FullHeight)));
        return Resize(new Thumb(image.Width, image.Height, bgra), size);
    }

    public static Thumb Resize(Thumb source, int size)
    {
        if (Math.Max(source.Width, source.Height) <= size) return source;
        using var image = new MagickImage(source.Bgra, new PixelReadSettings((uint)source.Width, (uint)source.Height, StorageType.Char, PixelMapping.BGRA));
        return ToThumb(image, size);
    }

    // ── Кеш мініатюр із правками: %LocalAppData%\Lumos\Thumbs ───────────────────

    private static readonly string CacheFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumos", "Thumbs");

    /// <summary>Ключ залежить від файлу фото й файлу правок: змінився хоч один — мініатюра перераховується.</summary>
    private static string CachePath(string photoPath, int size)
    {
        var photo = new FileInfo(photoPath);
        long edits = EditSidecar.LastWrite(photoPath)?.Ticks ?? 0;
        string key = $"{photo.FullName}|{photo.LastWriteTimeUtc.Ticks}|{edits}|{size}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())));
        return Path.Combine(CacheFolder, hash + ".jpg");
    }

    public static Thumb? FromCache(string photoPath, int size)
    {
        try
        {
            string path = CachePath(photoPath, size);
            if (!File.Exists(path)) return null;
            using var image = new MagickImage(path);
            return ToThumb(image, size);
        }
        catch (Exception ex) when (ex is MagickException or IOException)
        {
            return null;
        }
    }

    public static void SaveToCache(string photoPath, int size, Thumb thumb)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            using var image = new MagickImage(thumb.Bgra, new PixelReadSettings((uint)thumb.Width, (uint)thumb.Height, StorageType.Char, PixelMapping.BGRA));
            image.Quality = 90;
            image.Write(CachePath(photoPath, size), MagickFormat.Jpeg);
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException) { }
    }

    // ── Допоміжне ─────────────────────────────────────────────────────────────

    /// <summary>JPEG розкодовується одразу зменшеним (у рази швидше) — підказка «jpeg:size».</summary>
    private static MagickImage Read(object source, int size)
    {
        var settings = new MagickReadSettings();
        settings.SetDefine(MagickFormat.Jpeg, "size", $"{size * 2}x{size * 2}");
        return source switch
        {
            byte[] bytes => new MagickImage(bytes, settings),
            string path => new MagickImage(path, settings),
            _ => throw new ArgumentException(null, nameof(source)),
        };
    }

    private static Thumb ToThumb(MagickImage image, int size)
    {
        if (Math.Max(image.Width, image.Height) > size) image.Thumbnail(new MagickGeometry((uint)size, (uint)size));
        image.Alpha(AlphaOption.Off);
        using var pixels = image.GetPixelsUnsafe();
        byte[] rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("Не вдалося прочитати мініатюру.");
        int w = (int)image.Width, h = (int)image.Height;
        var bgra = new byte[w * h * 4];
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4)
        {
            bgra[o] = rgb[i + 2];
            bgra[o + 1] = rgb[i + 1];
            bgra[o + 2] = rgb[i];
            bgra[o + 3] = 255;
        }
        return new Thumb(w, h, bgra);
    }
}
