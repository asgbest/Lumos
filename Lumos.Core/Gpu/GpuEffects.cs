// Шейдери виконуються лише на відеокарті через DirectX 12 (Windows); вхід у них захищено перевіркою системи.
#pragma warning disable CA1416
using System.Runtime.Versioning;
using ComputeSharp;

namespace Lumos.Core.Gpu;

/// <summary>
/// <see cref="SpatialEffects"/> на відеокарті: ті самі кроки й формули (шумозаглушення, димка, чіткість, текстура, різкість)
/// над буфером RGB у відеопам'яті. Робочі буфери — з <see cref="GpuScratch"/>.
/// </summary>
[SupportedOSPlatform("windows6.2")]
internal static class GpuEffects
{
    public static void Apply(GpuBatch gpu, ReadWriteBuffer<float> rgb, int w, int h, DevelopSettings s, RenderFrame frame, GpuScratch scratch)
    {
        int n = w * h;
        var a = scratch.Plane(0, n);
        var b = scratch.Plane(1, n);
        var c = scratch.Plane(2, n);
        var tmp = scratch.Plane(3, n);
        double px = frame.Scale, longSide = frame.FullLongSide * px;

        if (s.ColorNoiseReduction > 0)
        {
            double sigma = s.ColorNoiseReduction / 100 * 6 * px;
            if (sigma >= 0.3)
            {
                gpu.For(w, h, new SplitChroma(rgb, a, b, c), rgb, a, b, c);
                GpuBlur.Gaussian(gpu, b, tmp, w, h, sigma);
                GpuBlur.Gaussian(gpu, c, tmp, w, h, sigma);
                gpu.For(w, h, new MergeChroma(rgb, a, b, c), rgb, a, b, c);
            }
        }
        if (s.NoiseReduction > 0)
        {
            double amount = s.NoiseReduction / 100;
            int r = Math.Max(1, (int)Math.Round(3 * px));
            gpu.For(w, h, new NoisePrepare(rgb, a, b, c), rgb, a, b, c);
            GpuBlur.Box(gpu, b, tmp, w, h, r);
            GpuBlur.Box(gpu, c, tmp, w, h, r);
            gpu.For(w, h, new NoiseCoefficients(b, c, (float)(amount * amount * 0.004)), b, c);
            GpuBlur.Box(gpu, b, tmp, w, h, r);
            GpuBlur.Box(gpu, c, tmp, w, h, r);
            gpu.For(w, h, new NoiseApply(rgb, a, b, c), rgb, a, b, c);
        }
        if (s.Dehaze != 0)
        {
            gpu.For(w, h, new DarkChannel(rgb, a), rgb, a);
            GpuBlur.Gaussian(gpu, a, tmp, w, h, 0.01 * longSide);
            gpu.For(w, h, new DehazeApply(rgb, a, (float)(s.Dehaze / 100)), rgb, a);
        }
        if (s.Clarity != 0 || s.Texture != 0 || s.SharpenAmount != 0)
            LocalContrast(gpu, rgb, w, h, s, px, longSide, a, b, c, tmp, scratch.Plane(4, n));
    }

    private static void LocalContrast(GpuBatch gpu, ReadWriteBuffer<float> rgb, int w, int h, DevelopSettings s, double px, double longSide,
        ReadWriteBuffer<float> logY, ReadWriteBuffer<float> blurA, ReadWriteBuffer<float> blurB, ReadWriteBuffer<float> tmp, ReadWriteBuffer<float> delta)
    {
        gpu.For(w, h, new LogLuma(rgb, logY, delta), rgb, logY, delta);

        if (s.Clarity != 0)
        {
            gpu.Copy(logY, blurA, w, h);
            GpuBlur.Gaussian(gpu, blurA, tmp, w, h, 0.012 * longSide);
            gpu.For(w, h, new AddClarity(logY, blurA, delta, (float)(s.Clarity / 100 * 0.55)), logY, blurA, delta);
        }
        if (s.Texture != 0)
        {
            gpu.Copy(logY, blurA, w, h);
            gpu.Copy(logY, blurB, w, h);
            GpuBlur.Gaussian(gpu, blurA, tmp, w, h, Math.Max(0.6 * px, 0.0008 * longSide));
            GpuBlur.Gaussian(gpu, blurB, tmp, w, h, 0.004 * longSide);
            gpu.For(w, h, new AddTexture(blurA, blurB, delta, (float)(s.Texture / 100 * 0.8)), blurA, blurB, delta);
        }
        if (s.SharpenAmount != 0)
        {
            double sigma = s.SharpenRadius * px;
            if (sigma >= 0.25)
            {
                gpu.Copy(logY, blurA, w, h);
                GpuBlur.Gaussian(gpu, blurA, tmp, w, h, sigma);
                float threshold = (float)(s.SharpenMasking / 100 * 0.35);
                if (threshold > 0)
                {
                    gpu.For(w, h, new EdgeStrength(blurA, blurB, w, h), blurA, blurB);
                    GpuBlur.Gaussian(gpu, blurB, tmp, w, h, Math.Max(1, sigma));
                }
                gpu.For(w, h, new AddSharpen(logY, blurA, blurB, delta,
                    (float)(s.SharpenAmount / 100 * 1.4), (float)((1 - s.SharpenDetail / 100) * 6), threshold), logY, blurA, blurB, delta);
            }
        }
        gpu.For(w, h, new ApplyDelta(rgb, delta), rgb, delta);
    }
}

/// <summary>
/// Робочі буфери у відеопам'яті для кадру одного розміру: площини для ефектів, результат ефектів без кешу
/// і готові кадри (основа й маска). Створюються, коли вперше знадобляться, і живуть між кадрами.
/// </summary>
[SupportedOSPlatform("windows6.2")]
internal sealed class GpuScratch(GraphicsDevice device, int pixels) : IDisposable
{
    private readonly ReadWriteBuffer<float>?[] _planes = new ReadWriteBuffer<float>?[5];
    private readonly ReadWriteBuffer<uint>?[] _outputs = new ReadWriteBuffer<uint>?[2];
    private ReadWriteBuffer<float>? _spatial;

    public int Pixels => pixels;

    public ReadWriteBuffer<float> Plane(int index, int length) => _planes[index] ??= device.AllocateReadWriteBuffer<float>(length);

    public ReadWriteBuffer<uint> Output(int index) => _outputs[index] ??= device.AllocateReadWriteBuffer<uint>(pixels);

    public ReadWriteBuffer<float> Spatial(int length) => _spatial ??= device.AllocateReadWriteBuffer<float>(length);

    public void Dispose()
    {
        foreach (var p in _planes) p?.Dispose();
        foreach (var o in _outputs) o?.Dispose();
        _spatial?.Dispose();
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct SplitChroma : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, y, cr, cb;

    public SplitChroma(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> y, ReadWriteBuffer<float> cr, ReadWriteBuffer<float> cb)
    {
        this.rgb = rgb; this.y = y; this.cr = cr; this.cb = cb;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float l = 0.2126f * rgb[3 * i] + 0.7152f * rgb[3 * i + 1] + 0.0722f * rgb[3 * i + 2];
        y[i] = l;
        cr[i] = rgb[3 * i] - l;
        cb[i] = rgb[3 * i + 2] - l;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct MergeChroma : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, y, cr, cb;

    public MergeChroma(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> y, ReadWriteBuffer<float> cr, ReadWriteBuffer<float> cb)
    {
        this.rgb = rgb; this.y = y; this.cr = cr; this.cb = cb;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float r = y[i] + cr[i], b = y[i] + cb[i];
        float g = (y[i] - 0.2126f * r - 0.0722f * b) / 0.7152f;
        rgb[3 * i] = Hlsl.Max(0, r);
        rgb[3 * i + 1] = Hlsl.Max(0, g);
        rgb[3 * i + 2] = Hlsl.Max(0, b);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct NoisePrepare : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, p, mean, mean2;

    public NoisePrepare(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> p, ReadWriteBuffer<float> mean, ReadWriteBuffer<float> mean2)
    {
        this.rgb = rgb; this.p = p; this.mean = mean; this.mean2 = mean2;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float v = Hlsl.Sqrt(Hlsl.Max(0, 0.2126f * rgb[3 * i] + 0.7152f * rgb[3 * i + 1] + 0.0722f * rgb[3 * i + 2]));
        p[i] = v;
        mean[i] = v;
        mean2[i] = v * v;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct NoiseCoefficients : IComputeShader
{
    private readonly ReadWriteBuffer<float> mean, mean2;
    private readonly float eps;

    public NoiseCoefficients(ReadWriteBuffer<float> mean, ReadWriteBuffer<float> mean2, float eps)
    {
        this.mean = mean; this.mean2 = mean2; this.eps = eps;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float m = mean[i], variance = Hlsl.Max(0, mean2[i] - m * m);
        float k = variance / (variance + eps);
        mean[i] = k;
        mean2[i] = m - k * m;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct NoiseApply : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, p, a, b;

    public NoiseApply(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> p, ReadWriteBuffer<float> a, ReadWriteBuffer<float> b)
    {
        this.rgb = rgb; this.p = p; this.a = a; this.b = b;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float before = p[i];
        float after = Hlsl.Max(0, a[i] * before + b[i]);
        float ratio = before > 1e-4f ? after * after / (before * before) : 1;
        rgb[3 * i] *= ratio;
        rgb[3 * i + 1] *= ratio;
        rgb[3 * i + 2] *= ratio;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct DarkChannel : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, dark;

    public DarkChannel(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> dark)
    {
        this.rgb = rgb; this.dark = dark;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        dark[i] = Hlsl.Clamp(Hlsl.Min(rgb[3 * i], Hlsl.Min(rgb[3 * i + 1], rgb[3 * i + 2])), 0, 1);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct DehazeApply : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, dark;
    private readonly float amount;

    public DehazeApply(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> dark, float amount)
    {
        this.rgb = rgb; this.dark = dark; this.amount = amount;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        for (int c = 0; c < 3; c++)
        {
            float v = rgb[3 * i + c];
            if (amount > 0)
            {
                float t = Hlsl.Max(0.2f, 1 - amount * 0.85f * dark[i]);
                v = (v - (1 - t)) / t;
            }
            else
            {
                v += (0.6f - v) * -amount * 0.5f;
            }
            rgb[3 * i + c] = Hlsl.Max(0, v);
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct LogLuma : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, logY, delta;

    public LogLuma(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> logY, ReadWriteBuffer<float> delta)
    {
        this.rgb = rgb; this.logY = logY; this.delta = delta;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        logY[i] = Hlsl.Log2(0.2126f * rgb[3 * i] + 0.7152f * rgb[3 * i + 1] + 0.0722f * rgb[3 * i + 2] + 1e-4f);
        delta[i] = 0;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct AddClarity : IComputeShader
{
    private readonly ReadWriteBuffer<float> logY, blur, delta;
    private readonly float k;

    public AddClarity(ReadWriteBuffer<float> logY, ReadWriteBuffer<float> blur, ReadWriteBuffer<float> delta, float k)
    {
        this.logY = logY; this.blur = blur; this.delta = delta; this.k = k;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float d = Hlsl.Clamp(logY[i] - blur[i], -2, 2);
        float mid = (logY[i] + 2.47f) / 2.5f;
        delta[i] += k * d * Hlsl.Exp(-mid * mid);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct AddTexture : IComputeShader
{
    private readonly ReadWriteBuffer<float> a, b, delta;
    private readonly float k;

    public AddTexture(ReadWriteBuffer<float> a, ReadWriteBuffer<float> b, ReadWriteBuffer<float> delta, float k)
    {
        this.a = a; this.b = b; this.delta = delta; this.k = k;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        delta[i] += k * Hlsl.Clamp(a[i] - b[i], -1.5f, 1.5f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct EdgeStrength : IComputeShader
{
    private readonly ReadWriteBuffer<float> blur, mask;
    private readonly int w, h;

    public EdgeStrength(ReadWriteBuffer<float> blur, ReadWriteBuffer<float> mask, int w, int h)
    {
        this.blur = blur; this.mask = mask; this.w = w; this.h = h;
    }

    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float gx = blur[y * w + Hlsl.Min(x + 1, w - 1)] - blur[y * w + Hlsl.Max(x - 1, 0)];
        float gy = blur[Hlsl.Min(y + 1, h - 1) * w + x] - blur[Hlsl.Max(y - 1, 0) * w + x];
        mask[y * w + x] = Hlsl.Sqrt(gx * gx + gy * gy);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct AddSharpen : IComputeShader
{
    private readonly ReadWriteBuffer<float> logY, blur, mask, delta;
    private readonly float amount, halo, threshold;

    public AddSharpen(ReadWriteBuffer<float> logY, ReadWriteBuffer<float> blur, ReadWriteBuffer<float> mask, ReadWriteBuffer<float> delta,
        float amount, float halo, float threshold)
    {
        this.logY = logY; this.blur = blur; this.mask = mask; this.delta = delta;
        this.amount = amount; this.halo = halo; this.threshold = threshold;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float d = logY[i] - blur[i];
        d /= 1 + Hlsl.Abs(d) * halo;
        float m = 1;
        if (threshold > 0)
        {
            float lo = threshold * 0.5f, hi = threshold * 1.5f;
            float t = Hlsl.Clamp((mask[i] - lo) / (hi - lo), 0, 1);
            m = t * t * (3 - 2 * t);
        }
        delta[i] += amount * d * m;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ApplyDelta : IComputeShader
{
    private readonly ReadWriteBuffer<float> rgb, delta;

    public ApplyDelta(ReadWriteBuffer<float> rgb, ReadWriteBuffer<float> delta)
    {
        this.rgb = rgb; this.delta = delta;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float f = Hlsl.Exp2(delta[i]);
        rgb[3 * i] *= f;
        rgb[3 * i + 1] *= f;
        rgb[3 * i + 2] *= f;
    }
}
