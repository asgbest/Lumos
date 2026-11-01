namespace Lumos.Core;

/// <summary>
/// Автоматичне прибирання хроматичних аберацій (кольорової облямівки на краях кадру). Бічна аберація — це червоний і синій
/// канали, трохи збільшені чи зменшені відносно зеленого. Шукаємо такий масштаб кожного з них, за якого перепади яскравості
/// вздовж радіуса збігаються з зеленим каналом. Працює на повному кадрі: на прев'ю зсув менший за піксель.
/// </summary>
public static class ChromaticAberration
{
    private const int MaxSamples = 300_000;

    /// <summary>Значення повзунків «Червоний» і «Синій» (−100…+100) для полотна без виправлень.</summary>
    public static (double Red, double Blue) Estimate(LinearImage canvas)
    {
        int w = canvas.Width, h = canvas.Height;
        float[] p = canvas.Pixels;
        double cx = w / 2.0, cy = h / 2.0, diag = Math.Sqrt(cx * cx + cy * cy);
        float white = Percentile(p, 0.995f);

        // Точки на краях (подалі від центру, де аберація помітна), з радіальним перепадом у зеленому, без пересвітів.
        var bag = new System.Collections.Concurrent.ConcurrentBag<(float X, float Y, float Ux, float Uy, float G)>();
        Parallel.For(0, h / 2, row =>
        {
            int y = row * 2;
            var local = new List<(float, float, float, float, float)>();
            for (int x = 0; x < w; x += 2)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy, r = Math.Sqrt(dx * dx + dy * dy);
                if (r < diag * 0.3) continue;
                double ux = dx / r, uy = dy / r;
                int i = (y * w + x) * 3;
                if (p[i] > white * 0.95f || p[i + 1] > white * 0.95f || p[i + 2] > white * 0.95f) continue;
                double g = Sample(p, w, h, x + 0.5 + ux, y + 0.5 + uy, 1, white) - Sample(p, w, h, x + 0.5 - ux, y + 0.5 - uy, 1, white);
                if (Math.Abs(g) > 0.04) local.Add(((float)(x + 0.5), (float)(y + 0.5), (float)ux, (float)uy, (float)g));
            }
            foreach (var s in local) bag.Add(s);
        });
        var samples = bag.OrderByDescending(s => Math.Abs(s.G)).Take(MaxSamples).ToArray();
        if (samples.Length < 500) return (0, 0);

        double Score(int channel, double slider)
        {
            double scale = 1 + slider * 2e-5;
            double dot = 0, norm = 0;
            foreach (var s in samples)
            {
                double qx = cx + (s.X - cx) * scale, qy = cy + (s.Y - cy) * scale;
                double g = Sample(p, w, h, qx + s.Ux, qy + s.Uy, channel, white) - Sample(p, w, h, qx - s.Ux, qy - s.Uy, channel, white);
                dot += g * s.G;
                norm += g * g;
            }
            return dot / Math.Sqrt(Math.Max(1e-12, norm));
        }

        double Best(int channel)
        {
            double best = 0, bestScore = Score(channel, 0);
            var coarse = Enumerable.Range(-50, 101).Select(i => i * 2.0).ToArray();
            var scores = new double[coarse.Length];
            Parallel.For(0, coarse.Length, i => scores[i] = Score(channel, coarse[i]));
            for (int i = 0; i < coarse.Length; i++)
                if (scores[i] > bestScore) (bestScore, best) = (scores[i], coarse[i]);
            double center = best;
            for (double v = center - 2; v <= center + 2; v += 0.5)
            {
                double sc = Score(channel, v);
                if (sc > bestScore) (bestScore, best) = (sc, v);
            }
            return Math.Clamp(best, -100, 100);
        }

        return (Best(0), Best(2));
    }

    private static float Percentile(float[] pixels, float q)
    {
        var sample = new float[Math.Min(200_000, pixels.Length / 3)];
        int step = Math.Max(1, pixels.Length / 3 / sample.Length);
        for (int i = 0; i < sample.Length; i++)
        {
            int j = i * step * 3;
            sample[i] = Math.Max(pixels[j], Math.Max(pixels[j + 1], pixels[j + 2]));
        }
        Array.Sort(sample);
        return Math.Max(1e-4f, sample[(int)((sample.Length - 1) * q)]);
    }

    /// <summary>Білінійно, у «гаммі» (корінь), щоб тіні й світлі ділянки важили порівну.</summary>
    private static double Sample(float[] p, int w, int h, double x, double y, int c, float white)
    {
        double fx = x - 0.5, fy = y - 0.5;
        int x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 1), y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 1);
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
        double a = p[(y0 * w + x0) * 3 + c], b = p[(y0 * w + x1) * 3 + c], d = p[(y1 * w + x0) * 3 + c], e = p[(y1 * w + x1) * 3 + c];
        double v = (a + (b - a) * tx) * (1 - ty) + (d + (e - d) * tx) * ty;
        return Math.Sqrt(Math.Max(0, v / white));
    }
}
