// Шейдери виконуються лише на відеокарті через DirectX 12 (Windows); вхід у них захищено перевіркою системи.
#pragma warning disable CA1416
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ComputeSharp;

namespace Lumos.Core.Gpu;

/// <summary>
/// Повний рендер фото на відеокарті — те саме, що <see cref="Developer.Render"/> + маски з <see cref="PhotoRenderer"/>:
/// фото в відеопам'яті (завантажується один раз), ефекти (кешуються, як на процесорі), попіксельна обробка,
/// змішування масок — і назад лише готові байти кадру. Гістограма рахується на процесорі з цих байтів.
/// Уся робота з відеокартою — під одним замком (прев'ю, мініатюри й експорт можуть рахуватися одночасно).
/// </summary>
[SupportedOSPlatform("windows6.2")]
internal static class GpuDeveloper
{
    private static readonly Lock Gate = new();

    // Робочі буфери — окремо для кожного розміру кадру (прев'ю, мініатюри, експорт), щоб не перевиділяти їх туди-сюди.
    private static readonly List<GpuScratch> Workspaces = [];

    // Завантажені в відеопам'ять фото: останні кілька (прев'ю, повна роздільність, мініатюра).
    private static readonly List<(WeakReference<LinearImage> Image, ReadWriteBuffer<float> Buffer)> Images = [];
    private static readonly ConditionalWeakTable<float[], ReadOnlyBuffer<float>> Weights = [];

    public static bool TryRender(LinearImage image, DevelopSettings s, ToneProfile profile, byte[] bgra, int[] histogram,
        RenderFrame frame, PhotoRenderCache? cache, int highlightMask,
        Func<Mask, float[]> weightsFor, IReadOnlyList<(Mask Mask, int Index)> masks)
    {
        if (GpuContext.Device is not { } device) return false;
        try
        {
            lock (Gate)
            {
                int n = image.Width * image.Height, w = image.Width, h = image.Height;
                var src = Upload(device, image);
                var work = Workspace(device, n);
                var outBase = work.Output(0);

                // Увесь кадр (основа, маски, підсвічування) — одним пакетом; чекаємо лише раз, у кінці.
                using (var gpu = new GpuBatch(device))
                {
                    RenderInto(gpu, work, src, w, h, s, profile, frame, cache?.Base, outBase);
                    if (masks.Count > 0)
                    {
                        var layer = work.Output(1);
                        foreach (var (mask, index) in masks)
                        {
                            var inside = MaskSettings.Combine(s, mask.Local);
                            RenderInto(gpu, work, src, w, h, inside, profile, frame, cache?.ForMask(index), layer, cache?.Base);
                            gpu.For(w, h, new BlendLayer(outBase, layer, WeightsBuffer(device, weightsFor(mask))), outBase, layer);
                        }
                    }
                    if (highlightMask >= 0 && highlightMask < s.Masks.Count)
                        gpu.For(w, h, new Highlight(outBase, WeightsBuffer(device, weightsFor(s.Masks[highlightMask]))), outBase);
                }

                outBase.CopyTo(MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, n * 4)));
            }
            CountHistogram(bgra, histogram);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Відеокарта відмовила (драйвер, пам'ять, втрачений пристрій) — далі все на процесорі.
            GpuContext.Disable(ex);
            return false;
        }
    }

    /// <summary>Один прохід конвеєра (як <see cref="Developer.Render"/>) у буфер упакованих BGRA.</summary>
    private static void RenderInto(GpuBatch gpu, GpuScratch work, ReadWriteBuffer<float> src, int w, int h, DevelopSettings s, ToneProfile profile,
        RenderFrame frame, RenderCache? cache, ReadWriteBuffer<uint> output, RenderCache? shared = null)
    {
        var (lutR, lutG, lutB) = Developer.BuildToneLuts(s, profile);
        var lut = new float[Developer.LutSize * 3];
        lutR.CopyTo(lut, 0);
        lutG.CopyTo(lut, Developer.LutSize);
        lutB.CopyTo(lut, 2 * Developer.LutSize);
        var (wr, wg, wb) = Developer.WhiteBalanceGains(s);
        float exposure = MathF.Pow(2f, (float)s.Exposure + profile.BaseExposureEv);
        float gr = wr * exposure, gg = wg * exposure, gb = wb * exposure;

        var input = src;
        if (SpatialEffects.Active(s))
        {
            input = SpatialStage(gpu, work, src, w, h, s, frame, wr, wg, wb, cache, shared);
            gr = gg = gb = exposure;
        }

        var mixer = HslMixer.Create(s);
        var hsl = new float[360 * 3];
        if (mixer is not null)
        {
            mixer._shift.CopyTo(hsl, 0);
            mixer._saturation.CopyTo(hsl, 360);
            mixer._luminanceEv.CopyTo(hsl, 720);
        }
        var grading = ColorGrading.Create(s);
        var vignette = Vignette.Create(s, frame, w, h);
        var grain = Grain.Create(s, frame);

        var lutBuffer = Table(gpu, lut);
        var hslBuffer = Table(gpu, hsl);
        float[] gs = grading?._shadows ?? [0, 0, 0], gm = grading?._midtones ?? [0, 0, 0];
        float[] gh = grading?._highlights ?? [0, 0, 0], gl = grading?._global ?? [0, 0, 0];
        gpu.For(w, h, new Develop(input, lutBuffer, hslBuffer, output, w,
            new Float3(gr, gg, gb), 1f + (float)s.Saturation / 100f, (float)s.Vibrance / 100f,
            mixer is null ? 0 : 1,
            grading is null ? 0 : 1, new Float3(gs[0], gs[1], gs[2]), new Float3(gm[0], gm[1], gm[2]),
            new Float3(gh[0], gh[1], gh[2]), new Float3(gl[0], gl[1], gl[2]), grading?._exponent ?? 1, grading?._balance ?? 1,
            vignette is null ? 0 : 1, new Float4(vignette?._amount ?? 0, vignette?._inner ?? 0, vignette?._outer ?? 1, vignette?._power ?? 2),
            new Float4(vignette?._aspectX ?? 1, vignette?._aspectY ?? 1, vignette?._cx ?? 0, vignette?._cy ?? 0),
            new Float2(vignette?._halfW ?? 1, vignette?._halfH ?? 1),
            grain is null ? 0 : 1, new Float3(grain?._amount ?? 0, grain?._cell ?? 1, grain?._rough ?? 0),
            new Float3(grain?._scale ?? 1, grain?._ox ?? 0, grain?._oy ?? 0)), input, output);
    }

    /// <summary>Баланс білого + ефекти з сусідніми пікселями; результат кешується у відеопам'яті, як на процесорі.</summary>
    private static ReadWriteBuffer<float> SpatialStage(GpuBatch gpu, GpuScratch work, ReadWriteBuffer<float> src, int w, int h, DevelopSettings s,
        RenderFrame frame, float wr, float wg, float wb, RenderCache? cache, RenderCache? shared)
    {
        var key = (src, wr, wg, wb, s.Texture, s.Clarity, s.Dehaze, s.SharpenAmount, s.SharpenRadius, s.SharpenDetail,
            s.SharpenMasking, s.NoiseReduction, s.ColorNoiseReduction, frame.Scale, frame.FullLongSide);
        if (cache?.GpuKey is { } cached && cached.Equals(key) && cache.GpuBuffer is ReadWriteBuffer<float> ready) return ready;
        // Маска, що не змінює ні балансу білого, ні ефектів, бере вже готовий результат основного кадру.
        if (shared?.GpuKey is { } common && common.Equals(key) && shared.GpuBuffer is ReadWriteBuffer<float> same) return same;

        int count = w * h * 3;
        var device = gpu.Device;
        ReadWriteBuffer<float> dst;
        if (cache is not null)
        {
            if (cache.GpuBuffer is ReadWriteBuffer<float> reuse && reuse.Length == count) dst = reuse;
            else
            {
                if (cache.GpuBuffer is IDisposable old) gpu.Defer(old);
                dst = device.AllocateReadWriteBuffer<float>(count);
            }
        }
        else dst = work.Spatial(count);

        gpu.For(w, h, new WhiteBalance(src, dst, new Float3(wr, wg, wb)), src, dst);
        GpuEffects.Apply(gpu, dst, w, h, s, frame, work);
        if (cache is not null)
        {
            cache.GpuKey = key;
            cache.GpuBuffer = dst;
        }
        return dst;
    }

    // Таблиці тону й кольору, уже завантажені у відеопам'ять (за вмістом): поки тягнуть ефект чи маску, вони ті самі,
    // і кадр не чекає на завантаження (кожне завантаження — окремий обмін із відеокартою).
    private static readonly List<(float[] Data, ReadOnlyBuffer<float> Buffer)> Tables = [];

    private static ReadOnlyBuffer<float> Table(GpuBatch gpu, float[] data)
    {
        for (int i = 0; i < Tables.Count; i++)
            if (Tables[i].Data.AsSpan().SequenceEqual(data))
            {
                var hit = Tables[i];
                Tables.RemoveAt(i);
                Tables.Add(hit);
                return hit.Buffer;
            }
        if (Tables.Count >= 16)
        {
            gpu.Defer(Tables[0].Buffer); // може ще стояти в черзі цього пакета — звільнити після нього
            Tables.RemoveAt(0);
        }
        var buffer = gpu.Device.AllocateReadOnlyBuffer(data);
        Tables.Add((data, buffer));
        return buffer;
    }

    /// <summary>Робочий простір для кадру з <paramref name="pixels"/> пікселів; найдавніший звільняється, коли їх більше трьох.</summary>
    private static GpuScratch Workspace(GraphicsDevice device, int pixels)
    {
        int i = Workspaces.FindIndex(w => w.Pixels == pixels);
        var work = i >= 0 ? Workspaces[i] : new GpuScratch(device, pixels);
        if (i >= 0) Workspaces.RemoveAt(i);
        Workspaces.Add(work);
        if (Workspaces.Count > 3)
        {
            Workspaces[0].Dispose(); // попередній пакет уже виконано (усе під замком), тож буфери вільні
            Workspaces.RemoveAt(0);
        }
        return work;
    }

    private static ReadWriteBuffer<float> Upload(GraphicsDevice device, LinearImage image)
    {
        Images.RemoveAll(e => !e.Image.TryGetTarget(out _) && Dispose(e.Buffer));
        foreach (var (reference, buffer) in Images)
            if (reference.TryGetTarget(out var known) && ReferenceEquals(known, image)) return buffer;
        if (Images.Count >= 3)
        {
            Images[0].Buffer.Dispose();
            Images.RemoveAt(0);
        }
        var uploaded = device.AllocateReadWriteBuffer(image.Pixels);
        Images.Add((new WeakReference<LinearImage>(image), uploaded));
        return uploaded;
    }

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return true;
    }

    private static ReadOnlyBuffer<float> WeightsBuffer(GraphicsDevice device, float[] weights) =>
        Weights.GetValue(weights, w => device.AllocateReadOnlyBuffer(w));


    private static void CountHistogram(byte[] bgra, int[] histogram)
    {
        const int bins = Developer.HistogramBins;
        Array.Clear(histogram);
        int pixels = bgra.Length / 4;
        Parallel.For(0, (pixels + 65535) / 65536, () => new int[bins * 3], (chunk, _, local) =>
        {
            int end = Math.Min(pixels, (chunk + 1) * 65536);
            for (int i = chunk * 65536; i < end; i++)
            {
                int o = i * 4;
                local[bgra[o + 2]]++;
                local[bins + bgra[o + 1]]++;
                local[2 * bins + bgra[o]]++;
            }
            return local;
        }, local =>
        {
            lock (histogram)
                for (int j = 0; j < local.Length; j++) histogram[j] += local[j];
        });
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct WhiteBalance : IComputeShader
{
    private readonly ReadWriteBuffer<float> src, dst;
    private readonly Float3 gains;

    public WhiteBalance(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, Float3 gains)
    {
        this.src = src; this.dst = dst; this.gains = gains;
    }

    public void Execute()
    {
        int i = (ThreadIds.Y * DispatchSize.X + ThreadIds.X) * 3;
        dst[i] = src[i] * gains.X;
        dst[i + 1] = src[i + 1] * gains.Y;
        dst[i + 2] = src[i + 2] * gains.Z;
    }
}

/// <summary>Попіксельна обробка — рядок у рядок як цикл у <see cref="Developer.Render"/>.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct Develop : IComputeShader
{
    private readonly ReadWriteBuffer<float> px;
    private readonly ReadOnlyBuffer<float> lut, hsl;
    private readonly ReadWriteBuffer<uint> output;
    private readonly int width;
    private readonly Float3 gain;
    private readonly float saturation, vibrance;
    private readonly int useHsl, useGrading;
    private readonly Float3 gradeShadows, gradeMid, gradeHigh, gradeGlobal;
    private readonly float gradeExponent, gradeBalance;
    private readonly int useVignette;
    private readonly Float4 vig1, vig2; // amount, inner, outer, power; aspectX, aspectY, cx, cy
    private readonly Float2 vigHalf;
    private readonly int useGrain;
    private readonly Float3 grain1, grain2; // amount, cell, rough; scale, ox, oy

    public Develop(ReadWriteBuffer<float> px, ReadOnlyBuffer<float> lut, ReadOnlyBuffer<float> hsl, ReadWriteBuffer<uint> output, int width,
        Float3 gain, float saturation, float vibrance, int useHsl,
        int useGrading, Float3 gradeShadows, Float3 gradeMid, Float3 gradeHigh, Float3 gradeGlobal, float gradeExponent, float gradeBalance,
        int useVignette, Float4 vig1, Float4 vig2, Float2 vigHalf, int useGrain, Float3 grain1, Float3 grain2)
    {
        this.px = px; this.lut = lut; this.hsl = hsl; this.output = output; this.width = width;
        this.gain = gain; this.saturation = saturation; this.vibrance = vibrance; this.useHsl = useHsl;
        this.useGrading = useGrading; this.gradeShadows = gradeShadows; this.gradeMid = gradeMid; this.gradeHigh = gradeHigh;
        this.gradeGlobal = gradeGlobal; this.gradeExponent = gradeExponent; this.gradeBalance = gradeBalance;
        this.useVignette = useVignette; this.vig1 = vig1; this.vig2 = vig2; this.vigHalf = vigHalf;
        this.useGrain = useGrain; this.grain1 = grain1; this.grain2 = grain2;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        int x = i % width, y = i / width;
        float r = px[3 * i] * gain.X, g = px[3 * i + 1] * gain.Y, b = px[3 * i + 2] * gain.Z;

        if (useVignette != 0)
        {
            float nx = Hlsl.Abs((x + 0.5f - vig2.Z) / vigHalf.X * vig2.X), ny = Hlsl.Abs((y + 0.5f - vig2.W) / vigHalf.Y * vig2.Y);
            float power = vig1.W;
            float d = power == 2 ? Hlsl.Sqrt(nx * nx + ny * ny) : Hlsl.Pow(Hlsl.Pow(nx, power) + Hlsl.Pow(ny, power), 1 / power);
            float f = 1;
            if (d > vig1.Y)
            {
                float t = Hlsl.Min(1, (d - vig1.Y) / (vig1.Z - vig1.Y));
                t = t * t * (3 - 2 * t);
                f = Hlsl.Exp(vig1.X * t * 0.6931472f);
            }
            r *= f; g *= f; b *= f;
        }

        if (useHsl != 0)
        {
            float max = Hlsl.Max(r, Hlsl.Max(g, b)), min = Hlsl.Min(r, Hlsl.Min(g, b));
            float delta = max - min;
            if (max > 1e-6f && delta > 1e-6f * max)
            {
                float sat = delta / max;
                float hue = max == r ? 60 * ((g - b) / delta) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
                int idx = (int)hue;
                if (idx >= 360) idx = 0;
                float weight = Hlsl.Min(1, sat * 4);
                float newHue = hue + hsl[idx] * weight;
                if (newHue < 0) newHue += 360;
                else if (newHue >= 360) newHue -= 360;
                float newSat = Hlsl.Min(1, sat * hsl[360 + idx]);
                float value = max * Hlsl.Exp2(hsl[720 + idx] * weight);

                float c = value * newSat, hp = newHue / 60f;
                float xx = c * (1 - Hlsl.Abs(hp - 2 * Hlsl.Floor(hp / 2) - 1)), m = value - c;
                int sector = (int)hp;
                if (sector == 0) { r = c; g = xx; b = 0; }
                else if (sector == 1) { r = xx; g = c; b = 0; }
                else if (sector == 2) { r = 0; g = c; b = xx; }
                else if (sector == 3) { r = 0; g = xx; b = c; }
                else if (sector == 4) { r = xx; g = 0; b = c; }
                else { r = c; g = 0; b = xx; }
                r += m; g += m; b += m;
            }
        }

        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        float mx = Hlsl.Max(r, Hlsl.Max(g, b)), mn = Hlsl.Min(r, Hlsl.Min(g, b));
        float chroma = mx > 1e-6f ? (mx - mn) / mx : 0f;
        float k = Hlsl.Max(0f, saturation + vibrance * (1f - chroma));
        r = Hlsl.Max(0f, lum + (r - lum) * k);
        g = Hlsl.Max(0f, lum + (g - lum) * k);
        b = Hlsl.Max(0f, lum + (b - lum) * k);

        const int lutLast = 16383;
        const float lutScale = 1f / 16f;
        float vr = lut[Hlsl.Min(lutLast, (int)(Hlsl.Sqrt(r * lutScale) * lutLast + 0.5f))];
        float vg = lut[16384 + Hlsl.Min(lutLast, (int)(Hlsl.Sqrt(g * lutScale) * lutLast + 0.5f))];
        float vb = lut[32768 + Hlsl.Min(lutLast, (int)(Hlsl.Sqrt(b * lutScale) * lutLast + 0.5f))];

        if (useGrading != 0)
        {
            float luma = Hlsl.Clamp(0.2126f * vr + 0.7152f * vg + 0.0722f * vb, 0, 1);
            float l = Hlsl.Pow(luma, gradeBalance);
            float ws = Hlsl.Pow(1 - l, gradeExponent), wh = Hlsl.Pow(l, gradeExponent);
            float wm = Hlsl.Max(0, 1 - ws - wh);
            vr += ws * gradeShadows.X + wm * gradeMid.X + wh * gradeHigh.X + gradeGlobal.X;
            vg += ws * gradeShadows.Y + wm * gradeMid.Y + wh * gradeHigh.Y + gradeGlobal.Y;
            vb += ws * gradeShadows.Z + wm * gradeMid.Z + wh * gradeHigh.Z + gradeGlobal.Z;
        }

        if (useGrain != 0)
        {
            float before = 0.2126f * vr + 0.7152f * vg + 0.0722f * vb;
            float u = (x + grain2.Y) / grain2.X / grain1.Y, w = (y + grain2.Z) / grain2.X / grain1.Y;
            float noise = Value(u, w) * (1 - 0.5f * grain1.Z) + Value(u * 2.3f + 17, w * 2.3f + 31) * 0.5f * grain1.Z;
            float mid = Hlsl.Clamp(before, 0, 1);
            float shift = grain1.X * noise * (0.25f + 3 * mid * (1 - mid));
            vr += shift; vg += shift; vb += shift;
        }

        uint br = (uint)(Hlsl.Clamp(vr, 0f, 1f) * 255f + 0.5f);
        uint bg = (uint)(Hlsl.Clamp(vg, 0f, 1f) * 255f + 0.5f);
        uint bb = (uint)(Hlsl.Clamp(vb, 0f, 1f) * 255f + 0.5f);
        output[i] = bb | (bg << 8) | (br << 16) | 0xFF000000u;
    }

    private static float Value(float u, float v)
    {
        int x0 = (int)Hlsl.Floor(u), y0 = (int)Hlsl.Floor(v);
        float tx = u - x0, ty = v - y0;
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        float a = Hash(x0, y0), b = Hash(x0 + 1, y0), c = Hash(x0, y0 + 1), d = Hash(x0 + 1, y0 + 1);
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    private static float Hash(int x, int y)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFFu) / (float)0xFFFFFF * 2 - 1;
    }
}

/// <summary>Змішування кадру маски з основним за силою маски — як у <see cref="PhotoRenderer"/> (у байтах, з округленням).</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BlendLayer : IComputeShader
{
    private readonly ReadWriteBuffer<uint> target, layer;
    private readonly ReadOnlyBuffer<float> weights;

    public BlendLayer(ReadWriteBuffer<uint> target, ReadWriteBuffer<uint> layer, ReadOnlyBuffer<float> weights)
    {
        this.target = target; this.layer = layer; this.weights = weights;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float a = weights[i];
        if (a <= 0) return;
        uint t = target[i], l = layer[i], result = 0xFF000000u;
        for (int c = 0; c < 3; c++)
        {
            int shift = c * 8;
            float tv = (t >> shift) & 0xFFu, lv = (l >> shift) & 0xFFu;
            uint v = (uint)(tv + (lv - tv) * a + 0.5f);
            result |= (v & 0xFFu) << shift;
        }
        target[i] = result;
    }
}

/// <summary>Червоне підсвічування маски (O).</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct Highlight : IComputeShader
{
    private readonly ReadWriteBuffer<uint> target;
    private readonly ReadOnlyBuffer<float> weights;

    public Highlight(ReadWriteBuffer<uint> target, ReadOnlyBuffer<float> weights)
    {
        this.target = target; this.weights = weights;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        float a = weights[i] * 0.5f;
        if (a <= 0) return;
        uint t = target[i];
        float b = t & 0xFFu, g = (t >> 8) & 0xFFu, r = (t >> 16) & 0xFFu;
        uint nb = (uint)(b * (1 - a) + 40 * a), ng = (uint)(g * (1 - a) + 40 * a), nr = (uint)(r * (1 - a) + 255 * a);
        target[i] = nb | (ng << 8) | (nr << 16) | 0xFF000000u;
    }
}
