using System.Diagnostics;
using System.Runtime.CompilerServices;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lumos.Core;

/// <summary>
/// ШІ-видалення: нейромережа LaMa домальовує фон замість обведеного предмета.
/// Береться квадратна ділянка навколо мазка з запасом контексту, зводиться до 512×512, мережа домальовує дірку,
/// і результат зберігається в латці (PNG) — щоб не запускати мережу при кожному перерахунку й у файлі правок.
/// Лінійні значення кодуються в картинку з множником (світле не обрізається), а при накладанні декодуються назад,
/// тож латка проходить увесь конвеєр правок разом із фото.
/// </summary>
public static class Inpainter
{
    public const int Side = 512;

    public sealed record Result(HealSpot Spot, bool Gpu, long Milliseconds);

    /// <summary>
    /// Найбільша довжина частини мазка, у пікселях полотна (але не менше кількох діаметрів пензля). Ділянка навколо частини —
    /// близько 1000 px, тож у 512×512 мережа бачить її приблизно вдвічі зменшеною, а не в 10–12 разів, як цілий дріт через кадр.
    /// </summary>
    internal static double PartLength = 360;

    /// <param name="canvas">Полотно (вирівняне, до обрізки) з уже накладеними попередніми латками; що більша роздільність — то детальніша латка.</param>
    /// <param name="progress">Довгий мазок: (готово частин, усього) — щоб показати, скільки ще чекати.</param>
    public static Result Generate(LinearImage canvas, HealSpot spot, Action<int, int>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        int W = canvas.Width, H = canvas.Height;
        double hard = Math.Max(1, spot.Size * Math.Max(W, H) / 2);
        double radius = Healing.OuterRadius(spot, W, H);
        var dabs = MaskShapes.Dabs(spot.Points, W, H, Math.Max(1, hard * 0.25));
        var parts = Split(dabs, Math.Max(PartLength, 3 * radius), radius);
        double length = 0;
        for (int i = 1; i < dabs.Count; i++) length += Distance(dabs[i - 1], dabs[i]);
        bool thin = length > 8 * hard;
        if (parts.Count == 1)
        {
            var (one, gpu1) = GenerateOne(canvas, spot, thin);
            // Тонкий предмет зберігає зсув для зерна в SourceDX/DY — нехай він живе у частині, а сама латка лишається без «джерела».
            if (one.PatchMask is not null) one = spot with { Mode = HealMode.Remove, Parts = new([one]) };
            return new Result(one, gpu1, watch.ElapsedMilliseconds);
        }

        // Частини — по черзі на копії полотна: кожна наступна бачить уже прибрану попередню й продовжує її без шва.
        var work = new LinearImage(W, H, (float[])canvas.Pixels.Clone());
        var done = new List<HealSpot>();
        bool gpu = false;
        foreach (var part in parts)
        {
            progress?.Invoke(done.Count, parts.Count);
            var points = new EquatableList<MaskPoint>(part.Select(d => new MaskPoint(d.X / W, d.Y / H)));
            var (patched, onGpu) = GenerateOne(work, spot with { Points = points, Parts = null }, thin, dabs);
            gpu |= onGpu;
            done.Add(patched);
            Healing.Apply(work, [patched]);
        }
        var result = spot with { Mode = HealMode.Remove, Patch = null, Parts = new(done), SourceDX = 0, SourceDY = 0 };
        return new Result(result, gpu, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Ділить мазок на частини не довші за length (за габаритом); сусідні частини заходять одна на одну приблизно на радіус пензля.
    /// </summary>
    private static List<List<(double X, double Y)>> Split(List<(double X, double Y)> dabs, double length, double radius)
    {
        var parts = new List<List<(double X, double Y)>>();
        int start = 0;
        while (true)
        {
            double x0 = dabs[start].X, x1 = x0, y0 = dabs[start].Y, y1 = y0;
            int end = start;
            while (end + 1 < dabs.Count)
            {
                var d = dabs[end + 1];
                if (Math.Max(Math.Max(x1, d.X) - Math.Min(x0, d.X), Math.Max(y1, d.Y) - Math.Min(y0, d.Y)) > length) break;
                end++;
                x0 = Math.Min(x0, d.X); x1 = Math.Max(x1, d.X); y0 = Math.Min(y0, d.Y); y1 = Math.Max(y1, d.Y);
            }
            parts.Add(dabs.GetRange(start, end - start + 1));
            if (end + 1 >= dabs.Count) return parts;
            // Наступна частина починається трохи раніше — на радіус назад по мазку.
            int next = end;
            while (next > start + 1 && Distance(dabs[next - 1], dabs[end]) < radius) next--;
            start = next;
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <param name="wholeStroke">
    /// Для частини довгого мазка — увесь мазок: у ділянці дірка й там, де предмет іде далі. Інакше мережа бачить дріт,
    /// що входить у дірку й виходить з неї, — і домальовує його назад.
    /// </param>
    /// <param name="thin">Мазок довгий і вузький (уздовж дроту, гілки, лінії) — шукати в ньому тонкий предмет.</param>
    private static (HealSpot Spot, bool Gpu) GenerateOne(LinearImage canvas, HealSpot spot, bool thin, List<(double X, double Y)>? wholeStroke = null)
    {
        int W = canvas.Width, H = canvas.Height;
        double hard = Math.Max(1, spot.Size * Math.Max(W, H) / 2);
        double radius = Healing.OuterRadius(spot, W, H);
        var dabs = MaskShapes.Dabs(spot.Points, W, H, Math.Max(1, hard * 0.25));

        // Ділянка: предмет + контекст навколо (мережі треба бачити, що довкола, щоб продовжити фон).
        double x0 = dabs.Min(d => d.X) - radius, x1 = dabs.Max(d => d.X) + radius;
        double y0 = dabs.Min(d => d.Y) - radius, y1 = dabs.Max(d => d.Y) + radius;
        double size = Math.Max(x1 - x0, y1 - y0);
        double side = size + 2 * Math.Max(size * 0.6, 24);
        double rw = Math.Min(side, W), rh = Math.Min(side, H);
        double rx = Math.Clamp((x0 + x1) / 2 - rw / 2, 0, W - rw), ry = Math.Clamp((y0 + y1) / 2 - rh / 2, 0, H - rh);

        // Ділянка → 512×512 (при зменшенні — середнє кількох відліків, щоб не було «сходинок»).
        var linear = new float[Side * Side * 3];
        double fx = rw / Side, fy = rh / Side;
        int n = Math.Clamp((int)Math.Ceiling(Math.Max(fx, fy)), 1, 4);
        Parallel.For(0, Side, v =>
        {
            for (int u = 0; u < Side; u++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int sy = 0; sy < n; sy++)
                        for (int sx = 0; sx < n; sx++)
                            acc += Sample(canvas.Pixels, W, H, rx + (u + (sx + 0.5) / n) * fx - 0.5, ry + (v + (sy + 0.5) / n) * fy - 0.5, c);
                    linear[(v * Side + u) * 3 + c] = acc / (n * n);
                }
        });

        // Множник: найсвітліше (без 0,5 % викидів) — майже біле; так і темні кадри мережа бачить нормально.
        var brightest = new float[Side * Side];
        for (int i = 0; i < brightest.Length; i++) brightest[i] = Math.Max(linear[3 * i], Math.Max(linear[3 * i + 1], linear[3 * i + 2]));
        Array.Sort(brightest);
        float scale = 0.95f / Math.Max(brightest[(int)(brightest.Length * 0.995)], 1e-4f);

        // Маска дірки в 512×512: слід пензля (з м'яким краєм) трохи розширений — мережа має перемалювати й край предмета.
        int pw = (int)Math.Ceiling(rw), ph = (int)Math.Ceiling(rh), px0 = (int)rx, py0 = (int)ry;
        var holeDabs = wholeStroke?.Where(d => d.X > rx - radius && d.X < rx + rw + radius && d.Y > ry - radius && d.Y < ry + rh + radius).ToList() ?? dabs;
        var footprint = Healing.Footprint(holeDabs, radius, hard, px0, py0, pw, ph);
        // Тонкий предмет у довгому мазку (дріт): міняється лише він сам, решта сліду пензля лишається справжньою.
        // І не мережею: вона бачить дріт, що входить у дірку й виходить з неї, — і домальовує його назад (або кольорові плями).
        // Місце дроту заповнюється плавно з фону по обидва боки, а зерно береться поруч (див. Blend).
        var thinMask = thin ? ThinObject(canvas, footprint, px0, py0, pw, ph, hard) : null;
        float[] output;
        bool gpu = false;
        (double DX, double DY) grain = (0, 0);
        if (thinMask is not null)
        {
            output = SmoothFill(canvas, thinMask, px0, py0, pw, ph, rx, ry, fx, fy, n, scale);
            // Зерно — з-за краю смуги, упоперек мазка (з того боку, що в кадрі).
            var (ax, ay) = dabs[0];
            var (bx, by) = dabs[^1];
            double len = Math.Max(1e-6, Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
            double nx = -(by - ay) / len, ny = (bx - ax) / len, shift = Math.Max(6, hard * 1.2);
            if (len < 1) (nx, ny) = (0, 1);
            double cx = (ax + bx) / 2, cy = (ay + by) / 2;
            if (cx + nx * shift < 0 || cx + nx * shift >= W || cy + ny * shift < 0 || cy + ny * shift >= H) (nx, ny) = (-nx, -ny);
            grain = (nx * shift / W, ny * shift / H);
        }
        else
        {
            var hole = new float[Side * Side];
            Parallel.For(0, Side, v =>
            {
                for (int u = 0; u < Side; u++)
                {
                    int sx = Math.Clamp((int)(rx + (u + 0.5) * fx) - px0, 0, pw - 1), sy = Math.Clamp((int)(ry + (v + 0.5) * fy) - py0, 0, ph - 1);
                    hole[v * Side + u] = footprint[sy * pw + sx] > 0.02f ? 1 : 0;
                }
            });
            // Запас навколо предмета — близько 8 пікселів полотна (не 4 пікселі мережі: при сильному зменшенні це була б широка смуга).
            hole = Dilate(hole, Math.Clamp((int)Math.Ceiling(8 / Math.Max(fx, fy)), 2, 4));

            var image = new DenseTensor<float>([1, 3, Side, Side]);
            var mask = new DenseTensor<float>([1, 1, Side, Side]);
            for (int v = 0; v < Side; v++)
                for (int u = 0; u < Side; u++)
                {
                    int i = v * Side + u;
                    for (int c = 0; c < 3; c++) image[0, c, v, u] = EncodeSrgb(Math.Clamp(linear[3 * i + c] * scale, 0, 1));
                    mask[0, 0, v, u] = hole[i];
                }

            output = AiSegmenter.Run(AiModels.Inpaint,
                [NamedOnnxValue.CreateFromTensor("image", image), NamedOnnxValue.CreateFromTensor("mask", mask)], out gpu);
        }

        // Латка — те, що намалювала мережа (RGB 0…255), у PNG.
        var rgb = new byte[Side * Side * 3];
        for (int v = 0; v < Side; v++)
            for (int u = 0; u < Side; u++)
                for (int c = 0; c < 3; c++)
                    rgb[(v * Side + u) * 3 + c] = (byte)Math.Clamp(output[(c * Side + v) * Side + u] + 0.5f, 0, 255);
        using var png = new MagickImage(rgb, new PixelReadSettings(Side, Side, StorageType.Char, PixelMapping.RGB));
        png.Format = MagickFormat.Png;

        // Де саме накладати латку (для тонкого предмета) — сіра PNG 512×512 у тому ж прямокутнику, що й латка.
        string? patchMask = null;
        if (thinMask is not null)
        {
            var gray = new byte[Side * Side * 3];
            Parallel.For(0, Side, v =>
            {
                for (int u = 0; u < Side; u++)
                {
                    // Максимум у клітинці, а не середнє: дріт тонший за клітинку не має «вицвісти» в масці.
                    int sx0 = Math.Clamp((int)(rx + u * fx) - px0, 0, pw - 1), sx1 = Math.Clamp((int)Math.Ceiling(rx + (u + 1) * fx) - px0, sx0 + 1, pw);
                    int sy0 = Math.Clamp((int)(ry + v * fy) - py0, 0, ph - 1), sy1 = Math.Clamp((int)Math.Ceiling(ry + (v + 1) * fy) - py0, sy0 + 1, ph);
                    float m = 0;
                    for (int y = sy0; y < sy1; y++)
                        for (int x = sx0; x < sx1; x++) m = Math.Max(m, thinMask[y * pw + x]);
                    int k = (v * Side + u) * 3;
                    gray[k] = gray[k + 1] = gray[k + 2] = (byte)Math.Round(m * 255);
                }
            });
            using var maskPng = new MagickImage(gray, new PixelReadSettings(Side, Side, StorageType.Char, PixelMapping.RGB)) { Format = MagickFormat.Png };
            maskPng.ColorType = ColorType.Grayscale;
            patchMask = maskPng.ToBase64();
        }

        var result = spot with
        {
            Mode = HealMode.Remove,
            Patch = png.ToBase64(),
            PatchMask = patchMask,
            PatchX = rx / W, PatchY = ry / H, PatchW = rw / W, PatchH = rh / H,
            PatchScale = scale,
            // У ШІ-видаленні джерела немає; для тонкого предмета тут — звідки брати зерно (зсув упоперек мазка).
            SourceDX = grain.DX, SourceDY = grain.DY,
        };
        return (result, gpu);
    }

    // Розкодовані латки (лінійні значення 512×512×3) — за рядком PNG, щоб не розпаковувати при кожному кадрі.
    private static readonly ConditionalWeakTable<string, float[]> Decoded = [];

    /// <summary>Накладає домальовану латку там, де пройшов пензель (з м'яким краєм).</summary>
    internal static void Blend(LinearImage img, HealSpot spot, float[] weight, int bx0, int by0, int bw, int bh, float opacity)
    {
        if (spot.Patch is not { } patch || spot.PatchW <= 0 || spot.PatchH <= 0) return;
        float scale = (float)spot.PatchScale;
        var linear = Decoded.GetValue(patch, p =>
        {
            using var image = new MagickImage(Convert.FromBase64String(p));
            var bytes = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = DecodeSrgb(i / 255f) / scale;
            var values = new float[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) values[i] = lut[bytes[i]];
            return values;
        });
        var where = spot.PatchMask is { } maskPng ? Decoded.GetValue(maskPng, p =>
        {
            using var image = new MagickImage(Convert.FromBase64String(p));
            return image.GetPixels().ToByteArray(PixelMapping.RGB)!.Select(b => b / 255f).ToArray();
        }) : null;

        int W = img.Width, H = img.Height;
        float[] px = img.Pixels;
        var alpha = new float[bw * bh];
        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                float a = weight[y * bw + x] * opacity;
                if (a <= 0) continue;
                int X = bx0 + x, Y = by0 + y;
                double u = ((X + 0.5) / W - spot.PatchX) / spot.PatchW * Side - 0.5;
                double v = ((Y + 0.5) / H - spot.PatchY) / spot.PatchH * Side - 0.5;
                if (u < -1 || v < -1 || u > Side || v > Side) continue;
                if (where is not null) a *= Sample(where, Side, Side, u, v, 0);
                alpha[y * bw + x] = a;
            }
        });

        // Тонкий предмет: латка плавна, тож зерно (дрібні деталі) береться з фото поруч — зі зсуву впоперек мазка.
        // Спершу зчитується, потім накладається: інакше потоки читали б уже змінені пікселі.
        float[]? grain = null;
        if (where is not null && (spot.SourceDX != 0 || spot.SourceDY != 0))
        {
            grain = new float[bw * bh * 3];
            int ox = (int)Math.Round(spot.SourceDX * W), oy = (int)Math.Round(spot.SourceDY * H);
            Parallel.For(0, bh, y =>
            {
                for (int x = 0; x < bw; x++)
                {
                    if (alpha[y * bw + x] <= 0) continue;
                    int sx = Math.Clamp(bx0 + x + ox, 2, W - 3), sy = Math.Clamp(by0 + y + oy, 2, H - 3);
                    for (int c = 0; c < 3; c++)
                    {
                        float mean = 0;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++) mean += px[((sy + dy) * W + sx + dx) * 3 + c];
                        grain[(y * bw + x) * 3 + c] = px[(sy * W + sx) * 3 + c] - mean / 25;
                    }
                }
            });
        }

        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                float a = alpha[y * bw + x];
                if (a <= 0) continue;
                int X = bx0 + x, Y = by0 + y;
                double u = ((X + 0.5) / W - spot.PatchX) / spot.PatchW * Side - 0.5;
                double v = ((Y + 0.5) / H - spot.PatchY) / spot.PatchH * Side - 0.5;
                int d = (Y * W + X) * 3;
                for (int c = 0; c < 3; c++)
                {
                    float target = Sample(linear, Side, Side, u, v, c) + (grain?[(y * bw + x) * 3 + c] ?? 0);
                    px[d + c] += (target - px[d + c]) * a;
                }
            }
        });
    }

    /// <summary>
    /// Тонкий предмет у сліді пензля (дріт, гілка, лінія): те, що морфологічне закриття (темне) чи відкриття (світле)
    /// розміром з пензель стирає, а шум неба — ні. Повертає м'яку маску «де накладати латку» з невеликим запасом
    /// або null, якщо предмет займає мало не весь пензель (тоді це не тонка лінія — латка йде на весь слід, як завжди).
    /// </summary>
    private static float[]? ThinObject(LinearImage canvas, float[] footprint, int px0, int py0, int pw, int ph, double hard)
    {
        int W = canvas.Width, H = canvas.Height;
        var lum = new float[pw * ph];
        Parallel.For(0, ph, y =>
        {
            int Y = Math.Min(py0 + y, H - 1);
            for (int x = 0; x < pw; x++)
            {
                int k = (Y * W + Math.Min(px0 + x, W - 1)) * 3;
                lum[y * pw + x] = 0.2126f * canvas.Pixels[k] + 0.7152f * canvas.Pixels[k + 1] + 0.0722f * canvas.Pixels[k + 2];
            }
        });
        int r = Math.Max(2, (int)(hard * 0.8));
        var closing = Morph(Morph(lum, pw, ph, r, max: true), pw, ph, r, max: false);
        var opening = Morph(Morph(lum, pw, ph, r, max: false), pw, ph, r, max: true);
        var diff = new float[lum.Length];
        for (int i = 0; i < lum.Length; i++)
            diff[i] = Math.Max(closing[i] - lum[i], lum[i] - opening[i]) / Math.Max(0.5f * (closing[i] + opening[i]), 1e-4f);

        // Поріг — над шумом: утричі більше за типову різницю в сліді пензля, і не менше 6 % яскравості.
        var core = Enumerable.Range(0, lum.Length).Where(i => footprint[i] > 0.5f).Select(i => diff[i]).ToArray();
        if (core.Length == 0) return null;
        Array.Sort(core);
        float threshold = Math.Max(0.06f, 3 * core[core.Length / 2]);
        var found = new float[lum.Length];
        int inCore = 0;
        for (int i = 0; i < lum.Length; i++)
            if (footprint[i] > 0.02f && diff[i] > threshold)
            {
                found[i] = 1;
                if (footprint[i] > 0.5f) inCore++;
            }
        if (inCore == 0 || inCore > core.Length * 0.45) return null;

        // Запас: край предмета (півтінь, ореол від різкості) — і м'який перехід до справжнього фону.
        int grow = Math.Max(3, (int)(hard * 0.2));
        var grown = Morph(found, pw, ph, grow, max: true);
        var soft = BoxBlur(grown, pw, ph, 2);
        for (int i = 0; i < soft.Length; i++) soft[i] = Math.Max(soft[i], found[i]) * Math.Min(1, footprint[i] * 4);
        return soft;
    }

    /// <summary>
    /// Плавне заповнення там, де mask > 0: значення підтягуються з фону навколо (піраміда «push-pull» — середнє відомих
    /// на грубших рівнях і назад), тож дріт не «продовжується», а небо по обидва боки зливається. Повертає латку 512×512
    /// (RGB 0…255, як вихід мережі) для прямокутника rx, ry з кроком fx, fy.
    /// </summary>
    private static float[] SmoothFill(LinearImage canvas, float[] mask, int px0, int py0, int pw, int ph,
        double rx, double ry, double fx, double fy, int n, float scale)
    {
        int W = canvas.Width, H = canvas.Height;
        var val = new float[pw * ph * 3];
        var known = new float[pw * ph];
        Parallel.For(0, ph, y =>
        {
            int Y = Math.Min(py0 + y, H - 1);
            for (int x = 0; x < pw; x++)
            {
                int i = y * pw + x, k = (Y * W + Math.Min(px0 + x, W - 1)) * 3;
                known[i] = mask[i] > 0.01f ? 0 : 1;
                for (int c = 0; c < 3; c++) val[i * 3 + c] = canvas.Pixels[k + c];
            }
        });
        PushPull(val, known, pw, ph);

        var output = new float[3 * Side * Side];
        Parallel.For(0, Side, v =>
        {
            for (int u = 0; u < Side; u++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int sy = 0; sy < n; sy++)
                        for (int sx = 0; sx < n; sx++)
                            acc += Sample(val, pw, ph, rx - px0 + (u + (sx + 0.5) / n) * fx - 0.5, ry - py0 + (v + (sy + 0.5) / n) * fy - 0.5, c);
                    output[(c * Side + v) * Side + u] = 255 * EncodeSrgb(Math.Clamp(acc / (n * n) * scale, 0, 1));
                }
        });
        return output;
    }

    /// <summary>Заповнює невідомі пікселі (known = 0) з грубших рівнів піраміди, де вони вже усереднені з відомих сусідів.</summary>
    private static void PushPull(float[] val, float[] known, int w, int h)
    {
        if (w <= 1 && h <= 1) return;
        bool any = false;
        for (int i = 0; i < known.Length && !any; i++) any = known[i] == 0;
        if (!any) return;

        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        var cval = new float[cw * ch * 3];
        var cknown = new float[cw * ch];
        Parallel.For(0, ch, y =>
        {
            for (int x = 0; x < cw; x++)
            {
                float s0 = 0, s1 = 0, s2 = 0, ws = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int X = 2 * x + dx, Y = 2 * y + dy;
                        if (X >= w || Y >= h) continue;
                        int i = Y * w + X;
                        float k = known[i];
                        s0 += val[i * 3] * k; s1 += val[i * 3 + 1] * k; s2 += val[i * 3 + 2] * k; ws += k;
                    }
                int j = y * cw + x;
                if (ws > 0) { cval[j * 3] = s0 / ws; cval[j * 3 + 1] = s1 / ws; cval[j * 3 + 2] = s2 / ws; cknown[j] = 1; }
            }
        });
        PushPull(cval, cknown, cw, ch);

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (known[i] > 0) continue;
                for (int c = 0; c < 3; c++) val[i * 3 + c] = Sample(cval, cw, ch, (x + 0.5) / 2 - 0.5, (y + 0.5) / 2 - 0.5, c);
            }
        });
    }

    /// <summary>Максимум (розширення) чи мінімум (звуження) у квадраті (2r+1)², окремо по рядках і стовпцях.</summary>
    private static float[] Morph(float[] src, int w, int h, int r, bool max)
    {
        if (r <= 0) return src;
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float m = src[y * w + x];
                for (int k = Math.Max(0, x - r), e = Math.Min(w - 1, x + r); k <= e; k++)
                    m = max ? Math.Max(m, src[y * w + k]) : Math.Min(m, src[y * w + k]);
                tmp[y * w + x] = m;
            }
        });
        Parallel.For(0, w, x =>
        {
            for (int y = 0; y < h; y++)
            {
                float m = tmp[y * w + x];
                for (int k = Math.Max(0, y - r), e = Math.Min(h - 1, y + r); k <= e; k++)
                    m = max ? Math.Max(m, tmp[k * w + x]) : Math.Min(m, tmp[k * w + x]);
                dst[y * w + x] = m;
            }
        });
        return dst;
    }

    private static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = Math.Max(0, x - r), e = Math.Min(w - 1, x + r); k <= e; k++) { s += src[y * w + k]; n++; }
                tmp[y * w + x] = s / n;
            }
        });
        Parallel.For(0, w, x =>
        {
            for (int y = 0; y < h; y++)
            {
                float s = 0; int n = 0;
                for (int k = Math.Max(0, y - r), e = Math.Min(h - 1, y + r); k <= e; k++) { s += tmp[k * w + x]; n++; }
                dst[y * w + x] = s / n;
            }
        });
        return dst;
    }

    private static float[] Dilate(float[] mask, int r)
    {
        var result = new float[mask.Length];
        Parallel.For(0, Side, y =>
        {
            for (int x = 0; x < Side; x++)
            {
                float m = 0;
                for (int dy = -r; dy <= r && m == 0; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= Side || sy >= Side || dx * dx + dy * dy > r * r) continue;
                        if (mask[sy * Side + sx] > 0) { m = 1; break; }
                    }
                result[y * Side + x] = m;
            }
        });
        return result;
    }

    private static float Sample(float[] px, int w, int h, double x, double y, int c)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = px[(y0 * w + x0) * 3 + c], b = px[(y0 * w + x1) * 3 + c];
        float d = px[(y1 * w + x0) * 3 + c], e = px[(y1 * w + x1) * 3 + c];
        return a + (b - a) * tx + (d - a) * ty + (a - b - d + e) * tx * ty;
    }

    private static float EncodeSrgb(float x) => x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;

    private static float DecodeSrgb(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
}
