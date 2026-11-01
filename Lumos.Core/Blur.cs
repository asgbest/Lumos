namespace Lumos.Core;

/// <summary>
/// Швидкі розмиття одноканального зображення (float, рядок за рядком).
/// Гаусове — трьома проходами «коробкою» (box blur): час не залежить від радіуса, тож і великий радіус чіткості,
/// і 24-мегапіксельний експорт рахуються швидко. Для дуже малих радіусів (різкість) — точне ядро Гауса.
/// Вертикальний прохід іде рядками з накопиченням по стовпцях — так пам'ять читається підряд і не гальмує.
/// </summary>
internal static class Blur
{
    /// <summary>Гаусове розмиття на місці; <paramref name="tmp"/> — робочий буфер того ж розміру.</summary>
    public static void Gaussian(float[] data, float[] tmp, int w, int h, double sigma)
    {
        if (sigma < 0.25) return;
        if (sigma < 2)
        {
            Kernel(data, tmp, w, h, sigma);
            return;
        }
        foreach (int size in BoxSizes(sigma))
        {
            int r = (size - 1) / 2;
            if (r < 1) continue;
            BoxHorizontal(data, tmp, w, h, r);
            BoxVertical(tmp, data, w, h, r);
        }
    }

    /// <summary>Середнє в квадраті (2r+1)², на місці.</summary>
    public static void Box(float[] data, float[] tmp, int w, int h, int r)
    {
        if (r < 1) return;
        BoxHorizontal(data, tmp, w, h, r);
        BoxVertical(tmp, data, w, h, r);
    }

    /// <summary>Розміри трьох «коробок», що разом дають Гаус із заданою сигмою.</summary>
    internal static int[] BoxSizes(double sigma)
    {
        const int n = 3;
        double ideal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        int upper = lower + 2;
        double mIdeal = (12 * sigma * sigma - n * lower * lower - 4 * n * lower - 3 * n) / (-4.0 * lower - 4);
        int m = (int)Math.Round(mIdeal);
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = i < m ? lower : upper;
        return sizes;
    }

    private static void BoxHorizontal(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float acc = 0;
            for (int x = -r; x <= r; x++) acc += src[row + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = acc * inv;
                acc += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        });
    }

    private static void BoxVertical(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        const int chunk = 256;
        int chunks = (w + chunk - 1) / chunk;
        Parallel.For(0, chunks, c =>
        {
            int x0 = c * chunk, x1 = Math.Min(w, x0 + chunk), cw = x1 - x0;
            var acc = new float[cw];
            for (int y = -r; y <= r; y++)
            {
                int row = Math.Clamp(y, 0, h - 1) * w;
                for (int i = 0; i < cw; i++) acc[i] += src[row + x0 + i];
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int add = Math.Min(y + r + 1, h - 1) * w, sub = Math.Max(y - r, 0) * w;
                for (int i = 0; i < cw; i++)
                {
                    dst[row + x0 + i] = acc[i] * inv;
                    acc[i] += src[add + x0 + i] - src[sub + x0 + i];
                }
            }
        });
    }

    /// <summary>Точне ядро Гауса (для сигми &lt; 2): по рядках, потім по стовпцях.</summary>
    private static void Kernel(float[] data, float[] tmp, int w, int h, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = new float[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++) sum += k[i + radius] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < k.Length; i++) k[i] /= (float)sum;

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int i = -radius; i <= radius; i++) acc += k[i + radius] * data[row + Math.Clamp(x + i, 0, w - 1)];
                tmp[row + x] = acc;
            }
        });
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) data[row + x] = 0;
            for (int i = -radius; i <= radius; i++)
            {
                int src = Math.Clamp(y + i, 0, h - 1) * w;
                float kv = k[i + radius];
                for (int x = 0; x < w; x++) data[row + x] += kv * tmp[src + x];
            }
        });
    }
}
