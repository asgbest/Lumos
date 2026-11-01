using ImageMagick;

namespace Lumos.Core;

/// <summary>
/// Дані зйомки (EXIF) для експортованого JPEG: камера, об'єктив, ISO, витримка, діафрагма, фокусна, дата зйомки
/// (а з фото телефона — і GPS). Без них галереї та телефони сортують експортовані фото за датою експорту, а не зйомки.
/// Поворот скидається на «як є» (пікселі вже повернуті), розмір — новий, мініатюра й службові дані виробника прибираються.
/// </summary>
public static class ExifMetadata
{
    /// <summary>EXIF з даних, які LibRaw прочитав із RAW.</summary>
    public static IExifProfile FromRaw(string make, string model, string lens, float iso, float shutter, float aperture,
        float focalLength, DateTime? taken, string artist)
    {
        var exif = new ExifProfile();
        if (make.Length > 0) exif.SetValue(ExifTag.Make, make);
        if (model.Length > 0) exif.SetValue(ExifTag.Model, model);
        if (lens.Length > 0) exif.SetValue(ExifTag.LensModel, lens);
        if (artist.Length > 0) exif.SetValue(ExifTag.Artist, artist);
        if (iso > 0) exif.SetValue(ExifTag.ISOSpeedRatings, [(ushort)Math.Min(ushort.MaxValue, Math.Round(iso))]);
        if (shutter > 0) exif.SetValue(ExifTag.ExposureTime, shutter < 1 ? new Rational(1, (uint)Math.Round(1 / shutter)) : new Rational(shutter));
        if (aperture > 0) exif.SetValue(ExifTag.FNumber, new Rational(aperture));
        if (focalLength > 0) exif.SetValue(ExifTag.FocalLength, new Rational(focalLength));
        if (taken is { } date)
        {
            string text = date.ToString("yyyy:MM:dd HH:mm:ss");
            exif.SetValue(ExifTag.DateTimeOriginal, text);
            exif.SetValue(ExifTag.DateTimeDigitized, text);
            exif.SetValue(ExifTag.DateTime, text);
        }
        return exif;
    }

    /// <summary>Готовий блок APP1 «Exif» для експорту або null, якщо даних немає.</summary>
    public static byte[]? ForExport(IExifProfile? source, int width, int height)
    {
        if (source is null) return null;
        var exif = new ExifProfile(source.ToByteArray());
        exif.SetValue(ExifTag.Orientation, (ushort)1);
        exif.SetValue(ExifTag.PixelXDimension, new Number((uint)width));
        exif.SetValue(ExifTag.PixelYDimension, new Number((uint)height));
        exif.SetValue(ExifTag.Software, "Lumos");
        exif.RemoveValue(ExifTag.JPEGInterchangeFormat);
        exif.RemoveValue(ExifTag.JPEGInterchangeFormatLength);
        byte[] bytes = WithHeader(exif.ToByteArray() ?? []);
        if (bytes.Length > MaxSegment)
        {
            // Службові дані виробника (MakerNote) бувають більші за допустимі 64 КБ — без них EXIF влазить.
            exif.RemoveValue(ExifTag.MakerNote);
            bytes = WithHeader(exif.ToByteArray() ?? []);
        }
        return bytes.Length is > 6 and <= MaxSegment ? bytes : null;
    }

    private const int MaxSegment = 65533;

    private static byte[] WithHeader(byte[] data)
    {
        byte[] header = "Exif\0\0"u8.ToArray();
        return data.AsSpan().StartsWith(header) ? data : [.. header, .. data];
    }

    /// <summary>Вставляє блок EXIF (APP1) одразу після початку JPEG — без перекодування зображення.</summary>
    public static byte[] InsertIntoJpeg(byte[] jpeg, byte[] exif)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        int length = exif.Length + 2;
        var result = new byte[jpeg.Length + exif.Length + 4];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        result[4] = (byte)(length >> 8);
        result[5] = (byte)length;
        exif.CopyTo(result, 6);
        Array.Copy(jpeg, 2, result, 6 + exif.Length, jpeg.Length - 2);
        return result;
    }
}
