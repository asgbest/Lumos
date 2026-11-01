namespace Lumos.Core;

/// <summary>Режими автоматичного вирівнювання перспективи (як Upright у Lightroom).</summary>
public enum UprightMode { Auto, Level, Vertical, Full }

/// <summary>
/// «Авто»-перспектива: знаходить на кадрі прямі відрізки (краї будинків, рам, горизонт) і підбирає нахил віртуальної камери
/// та поворот так, щоб вертикальні лінії стали вертикальними, а горизонтальні — горизонтальними.
/// Результат — звичайні значення повзунків «Перспективи», тож їх можна підправити руками.
/// </summary>
public static class Upright
{
    /// <summary>Відрізок у нормованих координатах полотна: (p − центр) / довша сторона.</summary>
    public readonly record struct Line(double X0, double Y0, double X1, double Y1)
    {
        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
    }

    public sealed record Result(double Vertical, double Horizontal, double Rotate, int Verticals, int Horizontals);

    private const int AnalysisSide = 1200;

    /// <summary>
    /// Кадр для аналізу: поворот, віддзеркалення й виправлення об'єктива (дисторсія гне прямі), без перспективи, вирівнювання й обрізки.
    /// </summary>
    public static LinearImage Canvas(LinearImage preview, DevelopSettings s)
    {
        var lens = new DevelopSettings
        {
            Rotation = s.Rotation, FlipH = s.FlipH, LensDistortion = s.LensDistortion,
            LensProfile = s.LensProfile, LensProfileData = s.LensProfileData,
        };
        return ImageGeometry.Apply(preview, lens, crop: false);
    }

    /// <summary>Підбирає повзунки для режиму; null — на кадрі замало прямих ліній.</summary>
    public static Result? Solve(LinearImage canvas, UprightMode mode)
    {
        var lines = DetectLines(canvas);
        // Вертикальні й горизонтальні — за напрямком на вихідному кадрі (до 30° від осі); похилі (дахи, тіні) не беремо.
        var verticals = new List<Line>();
        var horizontals = new List<Line>();
        foreach (var l in lines)
        {
            double dx = Math.Abs(l.X1 - l.X0), dy = Math.Abs(l.Y1 - l.Y0);
            double fromVertical = Math.Atan2(dx, dy) * 180 / Math.PI;
            if (fromVertical < 30) verticals.Add(l);
            else if (fromVertical > 60) horizontals.Add(l);
        }

        bool useVertical = mode != UprightMode.Level && Enough(verticals);
        // Горизонтальну перспективу «Авто» не чіпає: «горизонтальні» лінії часто лежать на землі (дорога, розмітка),
        // і їх «вирівнювання» сильно викривлює кадр. Її — лише в режимі «Повна».
        bool useHorizontal = mode == UprightMode.Full && Enough(horizontals);
        if (!Enough(verticals) && !Enough(horizontals)) return null;

        double totalLength = lines.Sum(l => l.Length);
        // «Авто» — як у Lightroom, без надмірних викривлень: штраф за сильний нахил.
        double penalty = mode == UprightMode.Auto ? 0.15 : 0;

        double Cost(double a, double b, double c)
        {
            var r = Warp.Rotation(a, b, c);
            double cost = 0;
            foreach (var l in verticals) cost += l.Length * Robust(Deviation(r, l, vertical: true));
            foreach (var l in horizontals) cost += l.Length * Robust(Deviation(r, l, vertical: false));
            double ta = a * 180 / Math.PI / 20, tb = b * 180 / Math.PI / 20;
            return cost / Math.Max(1e-9, totalLength) + penalty * (ta * ta + tb * tb);
        }

        double maxTilt = Warp.MaxTilt * Math.PI / 180, maxRotate = 10 * Math.PI / 180;
        double deg = Math.PI / 180;
        // Грубий перебір, потім уточнення по черзі за кожним параметром дрібнішими кроками.
        double bestA = 0, bestB = 0, bestC = 0, best = Cost(0, 0, 0);
        for (double a = useVertical ? -maxTilt : 0; a <= (useVertical ? maxTilt : 0) + 1e-9; a += 2.5 * deg)
            for (double b = useHorizontal ? -maxTilt : 0; b <= (useHorizontal ? maxTilt : 0) + 1e-9; b += 2.5 * deg)
                for (double c = -maxRotate; c <= maxRotate + 1e-9; c += 1 * deg)
                {
                    double v = Cost(a, b, c);
                    if (v < best) (best, bestA, bestB, bestC) = (v, a, b, c);
                }
        foreach (double step in new[] { 1.0, 0.5, 0.25, 0.1, 0.05, 0.02 })
            for (int pass = 0; pass < 3; pass++)
                foreach (int axis in new[] { 0, 1, 2 })
                {
                    if ((axis == 0 && !useVertical) || (axis == 1 && !useHorizontal)) continue;
                    foreach (int sign in new[] { -1, 1 })
                    {
                        double a = bestA + (axis == 0 ? sign * step * deg : 0);
                        double b = bestB + (axis == 1 ? sign * step * deg : 0);
                        double c = bestC + (axis == 2 ? sign * step * deg : 0);
                        if (Math.Abs(a) > maxTilt || Math.Abs(b) > maxTilt || Math.Abs(c) > maxRotate) continue;
                        double v = Cost(a, b, c);
                        if (v < best) (best, bestA, bestB, bestC) = (v, a, b, c);
                    }
                }

        // Чи лінії погоджуються з результатом: хоча б 40 % довжини тих ліній, на які спирається режим, мають стати рівними (до 1.5°).
        // Інакше це випадкові відрізки (дорога, гілки) — краще нічого не крутити.
        {
            var r = Warp.Rotation(bestA, bestB, bestC);
            double tolerance = 1.5 * deg, agreeing = 0, total = 0;
            bool checkVerticals = mode switch
            {
                UprightMode.Level => !Enough(horizontals),
                UprightMode.Full => Enough(verticals),
                _ => Enough(verticals),
            };
            bool checkHorizontals = mode switch
            {
                UprightMode.Level => Enough(horizontals),
                UprightMode.Full => Enough(horizontals),
                _ => !Enough(verticals),
            };
            foreach (var (family, vertical) in new[] { (verticals, true), (horizontals, false) })
            {
                if (vertical ? !checkVerticals : !checkHorizontals) continue;
                foreach (var l in family)
                {
                    total += l.Length;
                    if (Deviation(r, l, vertical) < tolerance) agreeing += l.Length;
                }
            }
            if (total <= 0 || agreeing / total < 0.4) return null;
        }

        // Назад у повзунки (див. Warp.Forward: «Вертикаль» має протилежний знак до нахилу).
        return new Result(
            Math.Round(-bestA / maxTilt * 100, 1),
            Math.Round(bestB / maxTilt * 100, 1),
            Math.Round(bestC / deg, 2),
            verticals.Count, horizontals.Count);
    }

    /// <summary>
    /// Чи ліній досить, щоб їм вірити: разом не менше 0.4 довшої сторони і або кілька, або одна довга (горизонт).
    /// Короткі випадкові «відрізки» на текстурах (листя, трава) не мають крутити кадр.
    /// </summary>
    private static bool Enough(List<Line> lines) =>
        lines.Sum(l => l.Length) >= 0.4 && (lines.Count >= 3 || lines.Any(l => l.Length >= 0.3));

    /// <summary>Кут (радіани) між перетвореним відрізком і віссю.</summary>
    private static double Deviation(double[] r, Line l, bool vertical)
    {
        var (x0, y0) = Project(r, l.X0, l.Y0);
        var (x1, y1) = Project(r, l.X1, l.Y1);
        double dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        return vertical ? Math.Atan2(dx, dy) : Math.Atan2(dy, dx);
    }

    private static (double X, double Y) Project(double[] r, double x, double y)
    {
        double w = r[6] * x + r[7] * y + r[8];
        if (w < 1e-3) w = 1e-3;
        return ((r[0] * x + r[1] * y + r[2]) / w, (r[3] * x + r[4] * y + r[5]) / w);
    }

    /// <summary>Коші: справжні похилі лінії, що потрапили в «вертикальні», не тягнуть результат на себе.</summary>
    private static double Robust(double angle)
    {
        const double sigma = 3 * Math.PI / 180;
        double t = angle / sigma;
        return Math.Log(1 + t * t);
    }

    /// <summary>
    /// Прямі відрізки (спрощений LSD): пікселі з сильним перепадом яскравості об'єднуються в області з однаковим напрямком краю,
    /// витягнуті й тонкі області — відрізки.
    /// </summary>
    public static List<Line> DetectLines(LinearImage canvas)
    {
        int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(canvas.Width, canvas.Height) / (double)AnalysisSide));
        int w = canvas.Width / factor, h = canvas.Height / factor;
        var lum = new float[w * h];
        float[] sp = canvas.Pixels;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double sum = 0;
                for (int j = 0; j < factor; j++)
                    for (int i = 0; i < factor; i++)
                    {
                        int s = ((y * factor + j) * canvas.Width + x * factor + i) * 3;
                        sum += 0.2126 * sp[s] + 0.7152 * sp[s + 1] + 0.0722 * sp[s + 2];
                    }
                lum[y * w + x] = (float)(sum / (factor * factor));
            }
        });
        // Яскравість — у гамму (≈ як бачить око), нормована на світлі ділянки кадру.
        var sorted = (float[])lum.Clone();
        Array.Sort(sorted);
        float white = Math.Max(1e-4f, sorted[(int)(sorted.Length * 0.99)]);
        for (int i = 0; i < lum.Length; i++) lum[i] = MathF.Sqrt(Math.Clamp(lum[i] / white, 0, 1));

        var mag = new float[w * h];
        var ang = new float[w * h];
        Parallel.For(1, h - 1, y =>
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float gx = (lum[i - w + 1] + 2 * lum[i + 1] + lum[i + w + 1] - lum[i - w - 1] - 2 * lum[i - 1] - lum[i + w - 1]) / 8;
                float gy = (lum[i + w - 1] + 2 * lum[i + w] + lum[i + w + 1] - lum[i - w - 1] - 2 * lum[i - w] - lum[i - w + 1]) / 8;
                mag[i] = MathF.Sqrt(gx * gx + gy * gy);
                ang[i] = MathF.Atan2(gy, gx);
            }
        });
        const float threshold = 0.02f;

        var order = Enumerable.Range(0, w * h).Where(i => mag[i] > threshold * 2).OrderByDescending(i => mag[i]).ToArray();
        var used = new bool[w * h];
        var lines = new List<Line>();
        var region = new List<int>();
        var stack = new Stack<int>();
        double minLength = Math.Max(20, 0.03 * Math.Max(w, h));
        double cos2Tol = Math.Cos(2 * 22.5 * Math.PI / 180);

        foreach (int seed in order)
        {
            if (used[seed]) continue;
            region.Clear();
            stack.Push(seed);
            used[seed] = true;
            double sc = Math.Cos(2 * ang[seed]), ss = Math.Sin(2 * ang[seed]);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                region.Add(i);
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 1 || ny < 1 || nx >= w - 1 || ny >= h - 1) continue;
                        int n = ny * w + nx;
                        if (used[n] || mag[n] <= threshold) continue;
                        // Той самий напрямок краю (з точністю до 180°: світле→темне і навпаки).
                        double len = Math.Sqrt(sc * sc + ss * ss);
                        double c2 = Math.Cos(2 * ang[n]), s2 = Math.Sin(2 * ang[n]);
                        if ((c2 * sc + s2 * ss) / Math.Max(1e-9, len) < cos2Tol) continue;
                        used[n] = true;
                        sc += c2;
                        ss += s2;
                        stack.Push(n);
                    }
            }
            if (region.Count < minLength * 0.8) continue;

            // Головна вісь області (PCA) — напрямок відрізка; розкид поперек — його товщина.
            double mx = 0, my = 0;
            foreach (int i in region) { mx += i % w; my += i / w; }
            mx /= region.Count;
            my /= region.Count;
            double cxx = 0, cyy = 0, cxy = 0;
            foreach (int i in region)
            {
                double dx = i % w - mx, dy = i / w - my;
                cxx += dx * dx; cyy += dy * dy; cxy += dx * dy;
            }
            double theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
            double ux = Math.Cos(theta), uy = Math.Sin(theta);
            double min = double.MaxValue, max = double.MinValue, across = 0;
            foreach (int i in region)
            {
                double dx = i % w - mx, dy = i / w - my;
                double t = dx * ux + dy * uy;
                min = Math.Min(min, t);
                max = Math.Max(max, t);
                across = Math.Max(across, Math.Abs(-dx * uy + dy * ux));
            }
            double length = max - min;
            if (length < minLength || across > 2.5 + length * 0.02 || region.Count < length * 0.9) continue;

            double s = Math.Max(w, h);
            lines.Add(new Line(
                (mx + ux * min + 0.5 - w / 2.0) / s, (my + uy * min + 0.5 - h / 2.0) / s,
                (mx + ux * max + 0.5 - w / 2.0) / s, (my + uy * max + 0.5 - h / 2.0) / s));
        }
        return lines;
    }
}
