using System.Runtime.CompilerServices;
using ImageMagick;

namespace Lumos.Core;

/// <summary>
/// Спільне для ШІ-масок і «✨ AI»: кадр «як є», маски від нейромереж, готові правки варіанта, його рендер і JPEG для Gemini.
/// </summary>
public static class AiLooks
{
    /// <summary>
    /// Кадр «як є» (стартові правки, поточна геометрія, без обрізки) у 8-бітному sRGB — те, що бачать нейромережі й Gemini,
    /// а не вже змінене повзунками фото.
    /// </summary>
    /// <param name="exposure">Додаткова експозиція, EV (для серії — автоматичне вирівнювання яскравості).</param>
    public static (byte[] Bgra, int Width, int Height) RenderAsShot(string path, LinearImage preview, DevelopSettings settings,
        ToneProfile profile, bool withHeals, double exposure = 0)
    {
        var look = DevelopSettings.DefaultFor(path);
        look.Exposure = exposure;
        look.CopyGeometryFrom(settings);
        if (withHeals) look.Heals = settings.Heals;
        var canvas = ImageGeometry.Apply(preview, look, crop: false);
        var bgra = new byte[canvas.Width * canvas.Height * 4];
        Developer.Render(canvas, look, profile, bgra, new int[Developer.HistogramBins * 3], RenderFrame.For(canvas, 1, 1));
        return (bgra, canvas.Width, canvas.Height);
    }

    /// <summary>ШІ-маска для кадру: нейромережа → маска-картинка для <see cref="Mask.Raster"/>.</summary>
    /// <param name="focus">Для «Об'єкта»: приблизна рамка потрібного об'єкта (центр і піврозміри, частки кадру); null — головний об'єкт.</param>
    public static (string Raster, AiSegmenter.Result Result) SegmentAiMask(AiMaskType type, string path, LinearImage preview,
        DevelopSettings settings, ToneProfile profile, (double X, double Y, double HalfW, double HalfH)? focus = null)
    {
        var (bgra, w, h) = RenderAsShot(path, preview, settings, profile, withHeals: false);
        if (focus is { } f && type.Model.Refocus)
        {
            var around = AiSegmenter.SegmentAround(type.Model, bgra, w, h, f.X, f.Y, f.HalfW, f.HalfH);
            return (MaskRaster.Encode(around.Mask, around.Width, around.Height), around);
        }
        // Той самий кадр — той самий ключ: друга маска «Ландшафту» (напр. рослинність після води) — без нового проходу мережі.
        string key = $"{path}|{RuntimeHelpers.GetHashCode(preview)}|{settings.Rotation}|{settings.FlipH}|{WarpParams.Of(settings)}";
        var r = AiSegmenter.Segment(type.Model, bgra, w, h, type.Channel, key);
        return (MaskRaster.Encode(r.Mask, r.Width, r.Height), r);
    }

    /// <summary>Готові правки варіанта: маски з формою (ШІ-маски рахуються тут, нейромережею). Пропущені — без моделі.</summary>
    public static DevelopSettings Materialize(AiSuggestion suggestion, string path, LinearImage preview, ToneProfile profile, List<string> skipped)
    {
        var settings = suggestion.Settings.Copy();
        var masks = new List<Mask>();
        foreach (var m in suggestion.Masks)
        {
            var mask = m.Shape with { Name = m.Name, Invert = m.Invert, Local = m.Local };
            if (AiMaskTypes.Of(m.Kind) is { } type)
            {
                if (!AiModels.IsInstalled(type.Model)) { skipped.Add(m.Name); continue; }
                // «Об'єкт» з рамкою від Gemini — саме той об'єкт, який він мав на увазі, а не той, що мережа вважає головним.
                (double, double, double, double)? focus = m.Kind == MaskKind.Subject && m.Shape.RadiusX > 0
                    ? (m.Shape.X1, m.Shape.Y1, m.Shape.RadiusX, m.Shape.RadiusY) : null;
                try { mask = mask with { Raster = SegmentAiMask(type, path, preview, settings, profile, focus).Raster }; }
                catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException) { skipped.Add(m.Name); continue; }
            }
            masks.Add(mask);
        }
        settings.Masks = new(masks);
        return settings;
    }

    /// <param name="crop">false — усе полотно (для Gemini: координати масок — у частках повного кадру).</param>
    public static Thumb Render(Photo photo, DevelopSettings settings, int size, bool crop)
    {
        var s = settings;
        if (!crop) s = settings with { CropX = 0, CropY = 0, CropW = 1, CropH = 1 };
        return Thumbnails.Edited(photo, s, size);
    }

    public static byte[] ToJpeg(byte[] bgra, int width, int height, int maxSide, int quality = 85)
    {
        using var image = new MagickImage(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaOption.Off);
        if (Math.Max(width, height) > maxSide) image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        image.Format = MagickFormat.Jpeg;
        image.Quality = (uint)quality;
        image.Strip();
        return image.ToByteArray();
    }
}
