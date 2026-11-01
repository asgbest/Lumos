namespace Lumos.Core;

/// <summary>Кеш між кадрами прев'ю: важкі ефекти — окремо для фото й кожної маски, плюс уже пораховані форми масок.</summary>
public sealed class PhotoRenderCache
{
    internal readonly RenderCache Base = new();
    private readonly Dictionary<int, RenderCache> _masks = [];
    private readonly Dictionary<object, float[]> _weights = [];

    internal RenderCache ForMask(int index)
    {
        if (!_masks.TryGetValue(index, out var cache)) _masks[index] = cache = new RenderCache();
        return cache;
    }

    internal float[] Weights(Mask mask, DevelopSettings s, RenderFrame frame, LinearImage image, RangeLook look)
    {
        // Форма не залежить від правок маски — тож, поки тягнуть її повзунки, маска не перераховується.
        // Маска за кольором/яскравістю залежить ще й від самого зображення та експозиції.
        bool range = mask.HasRange;
        var key = (mask with { Local = new DevelopSettings(), Name = "", Visible = true }, s.CropX, s.CropY, s.CropW, s.CropH, frame,
            image.Width, image.Height, range ? image : null, range ? look : default);
        if (_weights.TryGetValue(key, out var ready)) return ready;
        if (_weights.Count > 16) _weights.Clear();
        return _weights[key] = MaskShapes.Rasterize(mask, s, frame, image.Width, image.Height, image, look);
    }
}

/// <summary>
/// Повний рендер фото: спершу загальні правки, потім кожна маска — кадр із правками маски, змішаний із результатом
/// за силою маски в кожному пікселі. Так у масці працює весь набір інструментів, включно з HSL і грейдуванням.
/// Може підсвітити вибрану маску червоним (як «Показати накладання» в Lightroom).
/// </summary>
public static class PhotoRenderer
{
    private static readonly DevelopSettings Neutral = new();

    /// <param name="highlightMask">Індекс маски, яку підсвітити червоним; −1 — нічого.</param>
    public static void Render(LinearImage image, DevelopSettings s, ToneProfile profile, byte[] bgra, int[] histogram,
        RenderFrame frame, PhotoRenderCache? cache = null, int highlightMask = -1)
    {
        var active = s.Masks.Select((mask, index) => (mask, index)).Where(t => t.mask.Visible && t.mask.Local != Neutral).ToList();
        bool highlight = highlightMask >= 0 && highlightMask < s.Masks.Count;
        int w = image.Width, h = image.Height;
        var look = RangeLook.For(s, profile);
        float[] Weights(Mask mask) => cache?.Weights(mask, s, frame, image, look) ?? MaskShapes.Rasterize(mask, s, frame, w, h, image, look);

        // Спершу — відеокарта; якщо її немає чи вона відмовила, той самий результат рахує процесор.
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 2) && Gpu.GpuContext.Available
            && Gpu.GpuDeveloper.TryRender(image, s, profile, bgra, histogram, frame, cache, highlightMask, Weights, active))
            return;

        Developer.Render(image, s, profile, bgra, histogram, frame, cache?.Base);
        if (active.Count == 0 && !highlight) return;
        if (active.Count > 0)
        {
            var layer = new byte[bgra.Length];
            var unused = new int[histogram.Length];
            foreach (var (mask, index) in active)
            {
                var inside = MaskSettings.Combine(s, mask.Local);
                Developer.Render(image, inside, profile, layer, unused, frame, cache?.ForMask(index));
                var weights = Weights(mask);
                Parallel.For(0, h, y =>
                {
                    for (int x = 0, i = y * w; x < w; x++, i++)
                    {
                        float a = weights[i];
                        if (a <= 0) continue;
                        int o = i * 4;
                        for (int c = 0; c < 3; c++) bgra[o + c] = (byte)(bgra[o + c] + (layer[o + c] - bgra[o + c]) * a + 0.5f);
                    }
                });
            }
            RecountHistogram(bgra, histogram);
        }

        if (highlight)
        {
            var weights = Weights(s.Masks[highlightMask]);
            Parallel.For(0, h, y =>
            {
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    float a = weights[i] * 0.5f;
                    if (a <= 0) continue;
                    int o = i * 4;
                    bgra[o] = (byte)(bgra[o] * (1 - a) + 40 * a);
                    bgra[o + 1] = (byte)(bgra[o + 1] * (1 - a) + 40 * a);
                    bgra[o + 2] = (byte)(bgra[o + 2] * (1 - a) + 255 * a);
                }
            });
        }
    }

    private static void RecountHistogram(byte[] bgra, int[] histogram)
    {
        const int bins = Developer.HistogramBins;
        Array.Clear(histogram);
        for (int o = 0; o < bgra.Length; o += 4)
        {
            histogram[bgra[o + 2]]++;
            histogram[bins + bgra[o + 1]]++;
            histogram[2 * bins + bgra[o]]++;
        }
    }
}
