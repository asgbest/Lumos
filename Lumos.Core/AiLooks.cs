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

    /// <summary>
    /// «✨ AI Pro»: ШІ-видалення відволікань із плану — як «🪄» вручну, мазок уздовж рамки предмета. Латки рахуються один раз
    /// і йдуть у всі варіанти. Без моделі LaMa — нічого (завантаження посеред запиту не пропонуємо).
    /// </summary>
    public static List<(string Name, HealSpot Spot)> Remove(IReadOnlyList<AiStylist.AiRemoval> removals, LinearImage preview,
        DevelopSettings settings, CancellationToken cancel = default)
    {
        var spots = new List<(string, HealSpot)>();
        if (removals.Count == 0 || !AiModels.IsInstalled(AiModels.Inpaint)) return spots;
        var s = settings.Copy();
        foreach (var r in removals)
        {
            cancel.ThrowIfCancellationRequested();
            var canvas = ImageGeometry.Apply(preview, s, crop: false); // з уже прибраним — наступна латка бачить чистий кадр
            int w = canvas.Width, h = canvas.Height;
            double bw = 2 * r.HalfW * w, bh = 2 * r.HalfH * h;
            double diameter = Math.Max(6, Math.Min(bw, bh) * 1.15);
            bool horizontal = bw >= bh;
            double length = Math.Max(0, Math.Max(bw, bh) - diameter);
            int n = length < 1 ? 1 : (int)Math.Ceiling(length / (diameter / 3)) + 1;
            var points = new List<MaskPoint>();
            for (int i = 0; i < n; i++)
            {
                double t = n == 1 ? 0.5 : i / (double)(n - 1);
                double px = r.X * w + (horizontal ? (t - 0.5) * length : 0), py = r.Y * h + (horizontal ? 0 : (t - 0.5) * length);
                points.Add(new MaskPoint(Math.Clamp(px / w, 0, 1), Math.Clamp(py / h, 0, 1)));
            }
            var spot = new HealSpot(HealMode.Remove, diameter / Math.Max(w, h), 0.4, 1, new(points), 0, 0);
            try
            {
                var done = Inpainter.Generate(canvas, spot).Spot;
                spots.Add((r.Name, done));
                s.Heals = s.Heals.Add(done);
            }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException) { }
        }
        return spots;
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
