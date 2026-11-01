namespace Lumos.Core;

/// <summary>
/// Зображення в лінійному світлі: RGB float32 підряд (R, G, B, R, G, B…), основні кольори sRGB.
/// 1.0 — біла точка сенсора; після експозиції значення можуть бути більші за 1 (запас для світлих ділянок).
/// </summary>
public sealed class LinearImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }

    public LinearImage(int width, int height, float[] pixels)
    {
        if (pixels.Length != width * height * 3)
            throw new ArgumentException("Розмір масиву не відповідає ширині й висоті.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Зменшена копія для швидкого редагування (аналог Smart Preview): довша сторона — не більше <paramref name="maxSide"/>.
    /// Усереднення квадратами в лінійному світлі — так яскравість не спотворюється.
    /// </summary>
    public LinearImage Downscale(int maxSide) => DownscaleBy((int)Math.Ceiling(Math.Max(Width, Height) / (double)maxSide));

    /// <summary>Зменшена рівно в <paramref name="factor"/> разів (масштаби 50 %, 33 %, 25 %).</summary>
    public LinearImage DownscaleBy(int factor)
    {
        if (factor <= 1) return this;

        int w = Width / factor, h = Height / factor;
        var dst = new float[w * h * 3];
        float norm = 1f / (factor * factor);
        var src = Pixels;
        int srcWidth = Width;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int i = ((y * factor + dy) * srcWidth + x * factor) * 3;
                    for (int dx = 0; dx < factor; dx++, i += 3)
                    {
                        r += src[i];
                        g += src[i + 1];
                        b += src[i + 2];
                    }
                }
                int o = (y * w + x) * 3;
                dst[o] = r * norm;
                dst[o + 1] = g * norm;
                dst[o + 2] = b * norm;
            }
        });
        return new LinearImage(w, h, dst);
    }
}
