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
                File.Move(temp, jpegPath, overwrite: true);
            }
        }
    }
}
