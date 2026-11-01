using System.Runtime.CompilerServices;
using ImageMagick;

namespace Lumos.Core;

/// <summary>
/// Картинка-маска ШІ-масок у файлі правок: PNG у відтінках сірого, закодований base64 (довша сторона ≤ 1024 —
/// десятки кілобайтів, бо маски здебільшого рівні). Розкодована маска кешується, поки живий рядок.
/// </summary>
public static class MaskRaster
{
    public sealed record Decoded(float[] Data, int Width, int Height);

    private const int MaxSide = 1024;
    private static readonly ConditionalWeakTable<string, Decoded> Cache = [];

    public static string Encode(float[] mask, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < mask.Length; i++)
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)(Math.Clamp(mask[i], 0, 1) * 255 + 0.5f);
        using var image = new MagickImage(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        if (Math.Max(width, height) > MaxSide) image.Resize(new MagickGeometry(MaxSide, MaxSide));
        image.ColorType = ColorType.Grayscale;
        return Convert.ToBase64String(image.ToByteArray(MagickFormat.Png));
    }

    public static Decoded? Decode(string raster)
    {
        if (Cache.TryGetValue(raster, out var ready)) return ready;
        try
        {
            using var image = new MagickImage(Convert.FromBase64String(raster));
            int w = (int)image.Width, h = (int)image.Height;
            using var pixels = image.GetPixelsUnsafe();
            byte[] rgb = pixels.ToByteArray(PixelMapping.RGB) ?? [];
            var data = new float[w * h];
            for (int i = 0; i < data.Length; i++) data[i] = rgb[i * 3] / 255f;
            var decoded = new Decoded(data, w, h);
            Cache.AddOrUpdate(raster, decoded);
            return decoded;
        }
        catch (Exception ex) when (ex is FormatException or MagickException)
        {
            return null;
        }
    }
}
