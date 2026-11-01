namespace Lumos.Core;

/// <summary>
/// Геометрія кадру: поворот на 90°, віддзеркалення, вирівнювання на довільний кут і обрізка.
/// Порядок: спершу поворот/віддзеркалення («полотно»), потім вирівнювання навколо центру полотна,
/// і нарешті рамка обрізки — у частках 0…1 від розміру полотна.
/// </summary>
public static class ImageGeometry
{
    public static (int Width, int Height) OrientedSize(int width, int height, int rotation) =>
        rotation is 90 or 270 ? (height, width) : (width, height);

    public static bool IsFullCrop(DevelopSettings s) =>
        s.CropX <= 1e-6 && s.CropY <= 1e-6 && s.CropW >= 1 - 1e-6 && s.CropH >= 1 - 1e-6;

    /// <param name="crop">false — показати все полотно (з вирівнюванням), щоб поверх малювати рамку обрізки.</param>
    public static LinearImage Apply(LinearImage src, DevelopSettings s, bool crop)
    {
        LinearImage canvas = Orient(src, s.Rotation, s.FlipH);
        var warp = WarpParams.Of(s);
        bool warped = !warp.IsIdentity, cropped = crop && !IsFullCrop(s);
        if (s.Heals.Count > 0 || LensBlur.Active(s))
        {
            // Латки й розмиття об'єктива прив'язані до вирівняного полотна (як і маски): спершу полотно, на ньому вони, потім обрізка.
            canvas = warped ? WarpAndCrop(canvas, warp, 0, 0, 1, 1)
                : canvas == src ? new LinearImage(src.Width, src.Height, (float[])src.Pixels.Clone()) : canvas;
            Healing.Apply(canvas, s.Heals);
            LensBlur.Apply(canvas, s);
            return cropped ? CropExact(canvas, s.CropX, s.CropY, s.CropW, s.CropH) : canvas;
        }
        if (!warped && !cropped) return canvas;

        double cx = cropped ? s.CropX : 0, cy = cropped ? s.CropY : 0;
        double cw = cropped ? s.CropW : 1, ch = cropped ? s.CropH : 1;
        return warped ? WarpAndCrop(canvas, warp, cx, cy, cw, ch) : CropExact(canvas, cx, cy, cw, ch);
    }

    /// <summary>Розмір результату <see cref="Apply"/> без самої обробки.</summary>
    public static (int Width, int Height) OutputSize(int width, int height, DevelopSettings s, bool crop)
    {
        var (w, h) = OrientedSize(width, height, ((s.Rotation % 360) + 360) % 360);
        if (!crop || IsFullCrop(s)) return (w, h);
        return (Math.Max(1, (int)Math.Round(s.CropW * w)), Math.Max(1, (int)Math.Round(s.CropH * h)));
    }

    /// <summary>Копія прямокутної ділянки (для масштабу 100 %: рахуємо лише видиме плюс поле для ефектів).</summary>
    public static LinearImage Region(LinearImage src, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, src.Width - 1);
        y = Math.Clamp(y, 0, src.Height - 1);
        width = Math.Clamp(width, 1, src.Width - x);
        height = Math.Clamp(height, 1, src.Height - y);
        var dst = new float[width * height * 3];
        for (int row = 0; row < height; row++)
            Array.Copy(src.Pixels, ((y + row) * src.Width + x) * 3, dst, row * width * 3, width * 3);
        return new LinearImage(width, height, dst);
    }

    /// <summary>Поворот на 0/90/180/270° за годинниковою стрілкою, потім (за потреби) віддзеркалення зліва направо.</summary>
    private static LinearImage Orient(LinearImage src, int rotation, bool flip)
    {
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation == 0 && !flip) return src;

        int sw = src.Width, sh = src.Height;
        var (w, h) = OrientedSize(sw, sh, rotation);
        var dst = new float[w * h * 3];
        float[] sp = src.Pixels;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int ox = flip ? w - 1 - x : x;
                var (sx, sy) = rotation switch
                {
                    90 => (y, sh - 1 - ox),
                    180 => (sw - 1 - ox, sh - 1 - y),
                    270 => (sw - 1 - y, ox),
                    _ => (ox, y),
                };
                int s = (sy * sw + sx) * 3, d = (y * w + x) * 3;
                dst[d] = sp[s];
                dst[d + 1] = sp[s + 1];
                dst[d + 2] = sp[s + 2];
            }
        });
        return new LinearImage(w, h, dst);
    }

    private static LinearImage CropExact(LinearImage src, double cx, double cy, double cw, double ch)
    {
        int x0 = (int)Math.Round(cx * src.Width), y0 = (int)Math.Round(cy * src.Height);
        int w = Math.Clamp((int)Math.Round(cw * src.Width), 1, src.Width - x0);
        int h = Math.Clamp((int)Math.Round(ch * src.Height), 1, src.Height - y0);
        var dst = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            Array.Copy(src.Pixels, ((y0 + y) * src.Width + x0) * 3, dst, y * w * 3, w * 3);
        return new LinearImage(w, h, dst);
    }

    /// <summary>
    /// Об'єктив, перспектива, вирівнювання й обрізка за один прохід (бікубічна інтерполяція Кетмелла — Рома, щоб не м'якшало).
    /// Те, що виходить за фото, — чорне. Лише віньєтування — без перевибірки, тільки множення.
    /// </summary>
    private static LinearImage WarpAndCrop(LinearImage src, WarpParams p, double cx, double cy, double cw, double ch)
    {
        int sw = src.Width, sh = src.Height;
        int w = Math.Max(1, (int)Math.Round(cw * sw)), h = Math.Max(1, (int)Math.Round(ch * sh));
        int left = (int)Math.Round(cx * sw), top = (int)Math.Round(cy * sh);
        var warp = new Warp(p, sw, sh);
        var dst = new float[w * h * 3];
        float[] sp = src.Pixels;
        bool moves = p.MovesPixels;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int d = (y * w + x) * 3;
                if (!moves)
                {
                    int s = ((Math.Min(top + y, sh - 1)) * sw + Math.Min(left + x, sw - 1)) * 3;
                    float g = warp.VignetteGain(left + x + 0.5, top + y + 0.5);
                    dst[d] = sp[s] * g;
                    dst[d + 1] = sp[s + 1] * g;
                    dst[d + 2] = sp[s + 2] * g;
                    continue;
                }
                if (!warp.ToSource(left + x + 0.5, top + y + 0.5, out double sx, out double sy)) continue;
                double fx = sx - 0.5, fy = sy - 0.5;
                if (fx < -1 || fy < -1 || fx > sw || fy > sh) continue;
                float gain = warp.VignetteGain(sx, sy);
                if (warp.Chromatic)
                {
                    var (rx, ry) = warp.Channel(sx, sy, red: true);
                    var (bx, by) = warp.Channel(sx, sy, red: false);
                    dst[d] = Math.Max(0, Bicubic(sp, sw, sh, rx - 0.5, ry - 0.5, 0)) * gain;
                    dst[d + 1] = Math.Max(0, Bicubic(sp, sw, sh, fx, fy, 1)) * gain;
                    dst[d + 2] = Math.Max(0, Bicubic(sp, sw, sh, bx - 0.5, by - 0.5, 2)) * gain;
                }
                else
                {
                    BicubicRgb(sp, sw, sh, fx, fy, dst, d, gain);
                }
            }
        });
        return new LinearImage(w, h, dst);
    }

    private static void Weights(double t, Span<float> w)
    {
        // Кетмелл — Ром (a = −0.5).
        double t2 = t * t, t3 = t2 * t;
        w[0] = (float)(-0.5 * t3 + t2 - 0.5 * t);
        w[1] = (float)(1.5 * t3 - 2.5 * t2 + 1);
        w[2] = (float)(-1.5 * t3 + 2 * t2 + 0.5 * t);
        w[3] = (float)(0.5 * t3 - 0.5 * t2);
    }

    private static void BicubicRgb(float[] sp, int sw, int sh, double fx, double fy, float[] dst, int d, float gain)
    {
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        Weights(fx - x0, wx);
        Weights(fy - y0, wy);
        float r = 0, g = 0, b = 0;
        for (int j = 0; j < 4; j++)
        {
            int row = Math.Clamp(y0 - 1 + j, 0, sh - 1) * sw;
            float rr = 0, gg = 0, bb = 0;
            for (int i = 0; i < 4; i++)
            {
                int s = (row + Math.Clamp(x0 - 1 + i, 0, sw - 1)) * 3;
                rr += sp[s] * wx[i];
                gg += sp[s + 1] * wx[i];
                bb += sp[s + 2] * wx[i];
            }
            r += rr * wy[j];
            g += gg * wy[j];
            b += bb * wy[j];
        }
        dst[d] = Math.Max(0, r) * gain;
        dst[d + 1] = Math.Max(0, g) * gain;
        dst[d + 2] = Math.Max(0, b) * gain;
    }

    private static float Bicubic(float[] sp, int sw, int sh, double fx, double fy, int c)
    {
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        Weights(fx - x0, wx);
        Weights(fy - y0, wy);
        float sum = 0;
        for (int j = 0; j < 4; j++)
        {
            int row = Math.Clamp(y0 - 1 + j, 0, sh - 1) * sw;
            float acc = 0;
            for (int i = 0; i < 4; i++) acc += sp[(row + Math.Clamp(x0 - 1 + i, 0, sw - 1)) * 3 + c] * wx[i];
            sum += acc * wy[j];
        }
        return sum;
    }

    /// <summary>Чи рамка (у пікселях полотна) повністю лежить і на полотні, і на перетвореному фото (без чорних кутів).</summary>
    public static bool IsInside(double x, double y, double w, double h, Warp warp) => warp.Covers(x, y, w, h);

    /// <summary>
    /// Ставить найбільшу рамку з пропорціями <paramref name="aspect"/> (ширина/висота в пікселях),
    /// що вміщається у вирівняне й виправлене фото. Без пропорцій — зберігає пропорції поточної рамки.
    /// </summary>
    public static void FitCrop(DevelopSettings s, int canvasW, int canvasH, double? aspect)
    {
        double a = aspect ?? (s.CropW * canvasW) / Math.Max(1e-9, s.CropH * canvasH);
        var warp = new Warp(WarpParams.Of(s), canvasW, canvasH);
        var (cx, cy) = warp.Params.MovesPixels ? warp.SourceCenterOnCanvas() : (canvasW / 2.0, canvasH / 2.0);
        cx = Math.Clamp(cx, 0, canvasW);
        cy = Math.Clamp(cy, 0, canvasH);
        double limit = Math.Min(Math.Min(cy, canvasH - cy) * 2, Math.Min(cx, canvasW - cx) * 2 / a);

        // Найбільша висота, за якої рамка ще на фото (зростання рамки від центру — «монотонне»).
        double lo = 0, hi = limit;
        if (warp.Covers(cx - a * hi / 2, cy - hi / 2, a * hi, hi)) lo = hi;
        else
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                if (warp.Covers(cx - a * mid / 2, cy - mid / 2, a * mid, mid)) lo = mid; else hi = mid;
            }
        double h = Math.Max(lo, Math.Min(limit, 8)), w = a * h;

        s.CropW = Math.Min(1, w / canvasW);
        s.CropH = Math.Min(1, h / canvasH);
        s.CropX = Math.Clamp((cx - w / 2) / canvasW, 0, 1 - s.CropW);
        s.CropY = Math.Clamp((cy - h / 2) / canvasH, 0, 1 - s.CropH);
    }
}
