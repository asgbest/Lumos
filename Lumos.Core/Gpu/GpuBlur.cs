// Шейдери виконуються лише на відеокарті через DirectX 12 (Windows); вхід у них захищено перевіркою системи.
#pragma warning disable CA1416
using System.Runtime.Versioning;
using ComputeSharp;

namespace Lumos.Core.Gpu;

/// <summary>
/// Розмиття на відеокарті — ті самі, що <see cref="Blur"/>, і тим самим способом (ковзна сума «коробкою», три проходи
/// для Гауса, точне ядро для малих сигм), щоб результат збігався з процесорним.
/// Малі радіуси — потік на піксель, що сам сумує своє вікно; великі — ковзна сума потоком на рядок/стовпець.
/// </summary>
[SupportedOSPlatform("windows6.2")]
internal static class GpuBlur
{
    public static void Gaussian(GpuBatch gpu, ReadWriteBuffer<float> data, ReadWriteBuffer<float> tmp, int w, int h, double sigma)
    {
        if (sigma < 0.25) return;
        if (sigma < 2)
        {
            Kernel(gpu, data, tmp, w, h, sigma);
            return;
        }
        foreach (int size in Blur.BoxSizes(sigma))
        {
            int r = (size - 1) / 2;
            if (r < 1) continue;
            Box(gpu, data, tmp, w, h, r);
        }
    }

    /// <summary>
    /// Невеликі радіуси — потік на піксель, що сам сумує своє вікно (відеокарта завантажена повністю);
    /// великі — ковзна сума потоком на рядок/стовпець (менше читань пам'яті). Сума та сама, з тими ж краями.
    /// </summary>
    public static void Box(GpuBatch gpu, ReadWriteBuffer<float> data, ReadWriteBuffer<float> tmp, int w, int h, int r)
    {
        if (r < 1) return;
        if (r <= WindowLimit)
        {
            gpu.For(w, h, new BoxWindowRows(data, tmp, w, r), data, tmp);
            gpu.For(w, h, new BoxWindowColumns(tmp, data, w, h, r), tmp, data);
            return;
        }
        gpu.For(h, new BoxRows(data, tmp, w, r), data, tmp);
        gpu.For(w, new BoxColumns(tmp, data, w, h, r), tmp, data);
    }

    private const int WindowLimit = 96;

    private static void Kernel(GpuBatch gpu, ReadWriteBuffer<float> data, ReadWriteBuffer<float> tmp, int w, int h, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        // Ваги ядра рахує сам шейдер: завантаження таблиці у відеопам'ять чекало б на відеокарту посеред пакета.
        double sum = 0;
        for (int i = -radius; i <= radius; i++) sum += (float)Math.Exp(-i * i / (2 * sigma * sigma));
        float inv2s2 = (float)(1 / (2 * sigma * sigma)), invSum = 1f / (float)sum;
        gpu.For(w, h, new KernelRows(data, tmp, w, radius, inv2s2, invSum), data, tmp);
        gpu.For(w, h, new KernelColumns(tmp, data, w, h, radius, inv2s2, invSum), tmp, data);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BoxWindowRows : IComputeShader
{
    private readonly ReadWriteBuffer<float> src;
    private readonly ReadWriteBuffer<float> dst;
    private readonly int w;
    private readonly int r;

    public BoxWindowRows(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int r)
    {
        this.src = src;
        this.dst = dst;
        this.w = w;
        this.r = r;
    }

    public void Execute()
    {
        int x = ThreadIds.X, row = ThreadIds.Y * w;
        float acc = 0;
        for (int i = -r; i <= r; i++) acc += src[row + Hlsl.Clamp(x + i, 0, w - 1)];
        dst[row + x] = acc * (1f / (2 * r + 1));
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BoxWindowColumns : IComputeShader
{
    private readonly ReadWriteBuffer<float> src;
    private readonly ReadWriteBuffer<float> dst;
    private readonly int w;
    private readonly int h;
    private readonly int r;

    public BoxWindowColumns(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int r)
    {
        this.src = src;
        this.dst = dst;
        this.w = w;
        this.h = h;
        this.r = r;
    }

    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float acc = 0;
        for (int i = -r; i <= r; i++) acc += src[Hlsl.Clamp(y + i, 0, h - 1) * w + x];
        dst[y * w + x] = acc * (1f / (2 * r + 1));
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BoxRows : IComputeShader
{
    private readonly ReadWriteBuffer<float> src;
    private readonly ReadWriteBuffer<float> dst;
    private readonly int w;
    private readonly int r;

    public BoxRows(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int r)
    {
        this.src = src;
        this.dst = dst;
        this.w = w;
        this.r = r;
    }

    public void Execute()
    {
        int row = ThreadIds.X * w;
        float inv = 1f / (2 * r + 1);
        float acc = 0;
        for (int x = -r; x <= r; x++) acc += src[row + Hlsl.Clamp(x, 0, w - 1)];
        for (int x = 0; x < w; x++)
        {
            dst[row + x] = acc * inv;
            acc += src[row + Hlsl.Min(x + r + 1, w - 1)] - src[row + Hlsl.Max(x - r, 0)];
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BoxColumns : IComputeShader
{
    private readonly ReadWriteBuffer<float> src;
    private readonly ReadWriteBuffer<float> dst;
    private readonly int w;
    private readonly int h;
    private readonly int r;

    public BoxColumns(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int r)
    {
        this.src = src;
        this.dst = dst;
        this.w = w;
        this.h = h;
        this.r = r;
    }

    public void Execute()
    {
        int x = ThreadIds.X;
        float inv = 1f / (2 * r + 1);
        float acc = 0;
        for (int y = -r; y <= r; y++) acc += src[Hlsl.Clamp(y, 0, h - 1) * w + x];
        for (int y = 0; y < h; y++)
        {
            dst[y * w + x] = acc * inv;
            acc += src[Hlsl.Min(y + r + 1, h - 1) * w + x] - src[Hlsl.Max(y - r, 0) * w + x];
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct KernelRows : IComputeShader
{
    private readonly ReadWriteBuffer<float> src;
    private readonly ReadWriteBuffer<float> dst;
    private readonly int w;
    private readonly int radius;
    private readonly float inv2s2, invSum;

    public KernelRows(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int radius, float inv2s2, float invSum)
    {
        this.src = src;
        this.dst = dst;
        this.w = w;
        this.radius = radius;
        this.inv2s2 = inv2s2;
        this.invSum = invSum;
    }

    public void Execute()
    {
        int x = ThreadIds.X, row = ThreadIds.Y * w;
        float acc = 0;
        for (int i = -radius; i <= radius; i++) acc += Hlsl.Exp(-(i * i) * inv2s2) * invSum * src[row + Hlsl.Clamp(x + i, 0, w - 1)];
        dst[row + x] = acc;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct KernelColumns : IComputeShader
{
    private readonly ReadWriteBuffer<float> src;
    private readonly ReadWriteBuffer<float> dst;
    private readonly int w;
    private readonly int h;
    private readonly int radius;
    private readonly float inv2s2, invSum;

    public KernelColumns(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int radius, float inv2s2, float invSum)
    {
        this.src = src;
        this.dst = dst;
        this.w = w;
        this.h = h;
        this.radius = radius;
        this.inv2s2 = inv2s2;
        this.invSum = invSum;
    }

    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float acc = 0;
        for (int i = -radius; i <= radius; i++) acc += Hlsl.Exp(-(i * i) * inv2s2) * invSum * src[Hlsl.Clamp(y + i, 0, h - 1) * w + x];
        dst[y * w + x] = acc;
    }
}
