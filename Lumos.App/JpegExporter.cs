using Lumos.Core;
using SkiaSharp;

namespace Lumos.App;

/// <summary>
/// Експорт завжди найкращої якості, без налаштувань: повна роздільність сенсора, JPEG 100,
/// кольори без проріджування (4:4:4) і вбудований профіль sRGB.
/// Оригінал розкодовується заново, щоб не тримати в пам'яті повнорозмірну копію весь час редагування.
/// </summary>
public static class JpegExporter
{
    public static void Export(string photoPath, DevelopSettings settings, string jpegPath)
    {
        Photo photo = PhotoDecoder.Load(photoPath);
        LinearImage image = ImageGeometry.Apply(photo.Image, settings, crop: true);
        var bgra = new byte[image.Width * image.Height * 4];
        int fullLong = Math.Max(photo.Image.Width, photo.Image.Height);
        PhotoRenderer.Render(image, settings, photo.Profile, bgra, new int[Developer.HistogramBins * 3],
            RenderFrame.For(image, 1, fullLong));

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb());
        unsafe
        {
            fixed (byte* pixels = bgra)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)pixels, image.Width * 4);
                var options = new SKJpegEncoderOptions(100, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore);

                byte[] jpeg;
                using (var memory = new MemoryStream())
                {
                    using (var stream = new SKManagedWStream(memory))
                    {
                        if (!pixmap.Encode(stream, options))
                            throw new InvalidOperationException("Не вдалося закодувати JPEG.");
                    }
                    jpeg = memory.ToArray();
                }
                if (ExifMetadata.ForExport(photo.Exif, image.Width, image.Height) is { } exif)
                    jpeg = ExifMetadata.InsertIntoJpeg(jpeg, exif);

                // Теж через тимчасовий файл: обірваний експорт не зіпсує вже наявний JPEG.
                string temp = jpegPath + ".tmp";
                File.WriteAllBytes(temp, jpeg);
                MoveWithRetries(temp, jpegPath);
                // Перевіряємо, що файл справді на місці й повний, — інакше це помилка, а не «✅ Збережено».
                var saved = new FileInfo(jpegPath);
                if (!saved.Exists || saved.Length != jpeg.Length)
                    throw new IOException($"Файл {Path.GetFileName(jpegPath)} після запису не знайдено або він неповний.");
            }
        }
    }

    /// <summary>
    /// Свіжий файл на мить блокують OneDrive, антивірус чи переглядач фото («being used by another process») —
    /// пробуємо ще кілька разів; не вийшло — прибираємо тимчасовий файл і повідомляємо.
    /// </summary>
    private static void MoveWithRetries(string temp, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 40) // до ~10 с: OneDrive тримає файл, поки його вивантажує
                {
                    Thread.Sleep(250);
                    continue;
                }
                try { File.Delete(temp); } catch (Exception) { }
                throw new IOException($"Не вдалося записати {Path.GetFileName(target)}: {ex.Message} " +
                                      "(файл відкритий в іншій програмі чи його блокує OneDrive/антивірус).", ex);
            }
        }
    }
}

/// <summary>Журнал експорту (%LocalAppData%\Lumos\export.log): що, куди й чим скінчилось — щоб розібратися, якщо фото не з'явилось.</summary>
public static class ExportLog
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumos", "export.log");

    public static void Write(string source, string target, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string result = error is null ? $"OK {new FileInfo(target).Length / 1024} KB" : $"ПОМИЛКА {error.GetType().Name}: {error.Message}";
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {source} → {target}  {result}{Environment.NewLine}");
        }
        catch (Exception) { } // журнал — не причина зривати експорт
    }
}
