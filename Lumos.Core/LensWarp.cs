namespace Lumos.Core;

/// <summary>
/// Усе, що «рухає» пікселі полотна (крім повороту на 90° і віддзеркалення): виправлення об'єктива (дисторсія,
/// хроматичні аберації, віньєтування), перспектива (як «Transform» у Lightroom) і вирівнювання на кут.
/// </summary>
public readonly record struct WarpParams(
    double Angle, double Distortion, double CaRed, double CaBlue, double Vignette, double VignetteMid,
    double Vertical, double Horizontal, double Rotate, double Aspect, double Scale, double X, double Y, LensProfile? Profile = null)
{
    public static WarpParams Of(DevelopSettings s) => new(s.Angle, s.LensDistortion, s.LensCaRed, s.LensCaBlue, s.LensVignette,
        s.LensVignetteMid, s.PerspVertical, s.PerspHorizontal, s.PerspRotate, s.PerspAspect, s.PerspScale, s.PerspX, s.PerspY,
        s.LensProfile ? s.LensProfileData : null);

    private static bool Set(double v) => Math.Abs(v) > 1e-9;

    /// <summary>Чи пікселі зсуваються (тоді треба перевибірка); саме віньєтування лише множить яскравість.</summary>
    public bool MovesPixels => Set(Angle) || Set(Distortion) || Set(CaRed) || Set(CaBlue) || Perspective
        || Profile is { HasDistortion: true } or { HasChromatic: true };

    public bool Perspective => Set(Vertical) || Set(Horizontal) || Set(Rotate) || Set(Aspect) || Set(Scale - 100) || Set(X) || Set(Y);

    public bool IsIdentity => !MovesPixels && !Set(Vignette) && Profile is not { HasVignette: true };
}

/// <summary>
/// Відображення «точка полотна → точка фото» для <see cref="WarpParams"/> (полотно й фото — одного розміру).
/// Ланцюжок у зворотному порядку: вирівнювання → перспектива (обертання віртуальної камери + пропорції, масштаб, зсув)
/// → дисторсія об'єктива. Хроматичні аберації — свій радіальний масштаб для червоного й синього.
/// Координати безперервні: піксель i займає [i, i + 1).
/// </summary>
public sealed class Warp
{
    /// <summary>Повзунки «Вертикаль»/«Горизонталь» ±100 — нахил віртуальної камери до ±35°.</summary>
    public const double MaxTilt = 35;

    private readonly double _cx, _cy, _f, _invDiag2, _invHalfDiag, _cos, _sin, _k;
    private readonly double[] _inv = new double[9]; // перспектива навпаки, у нормованих координатах (p − центр) / f
    private readonly double _vignette, _vignetteExp;
    private readonly LensProfile? _profile;
    private readonly double _profileScale = 1;

    public WarpParams Params { get; }
    public int Width { get; }
    public int Height { get; }
    public double RedScale { get; }
    public double BlueScale { get; }
    public bool Chromatic { get; }

    public Warp(WarpParams p, int width, int height)
    {
        Params = p;
        Width = width;
        Height = height;
        _cx = width / 2.0;
        _cy = height / 2.0;
        _f = Math.Max(width, height);
        _invDiag2 = 1 / (_cx * _cx + _cy * _cy);
        _invHalfDiag = Math.Sqrt(_invDiag2);
        double a = p.Angle * Math.PI / 180;
        (_cos, _sin) = (Math.Cos(a), Math.Sin(a));
        // +100 прибирає «бочку»: точку краю беремо ближче до центру, тож краї розтягуються.
        _k = -0.3 * p.Distortion / 100;
        RedScale = 1 + p.CaRed * 2e-5;
        BlueScale = 1 + p.CaBlue * 2e-5;
        _profile = p.Profile;
        Chromatic = RedScale != 1 || BlueScale != 1 || _profile is { HasChromatic: true };
        _vignette = p.Vignette / 100 * 2; // +100 — кути світлішають на 2 ступені
        _vignetteExp = Math.Pow(2, (p.VignetteMid - 50) / 50);
        Invert3(Forward(p), _inv);
        if (_profile is { HasDistortion: true }) _profileScale = FitProfile();
    }

    /// <summary>
    /// Дисторсія з профілю зсуває край кадру: підбираємо масштаб, за якого край полотна лягає точно на край фото —
    /// без чорних кутів і без втрати країв (як Lightroom для вбудованих профілів), незалежно від рамки обрізки.
    /// </summary>
    private double FitProfile()
    {
        const int steps = 32;
        bool Fits(double g)
        {
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps * 2 - 1;
                foreach (var (x, y) in new[] { (t * _cx, -_cy), (t * _cx, _cy), (-_cx, t * _cy), (_cx, t * _cy) })
                {
                    double d = g * LensProfile.At(_profile!.Distortion, Math.Sqrt(x * x + y * y) * g * _invHalfDiag);
                    if (Math.Abs(x * d) > _cx + 0.25 || Math.Abs(y * d) > _cy + 0.25) return false;
                }
            }
            return true;
        }
        double lo = 0.5, hi = 1.5;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (Fits(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Перспектива вперед (нормовані координати фото → полотна): обертання камери Rx·Ry·Rz із проєкцією,
    /// центр лишається в центрі, далі пропорції, масштаб і зсув.
    /// </summary>
    private static double[] Forward(WarpParams p)
    {
        double a = -p.Vertical / 100 * MaxTilt * Math.PI / 180;  // мінус — верх ширшає (будинки, зняті знизу)
        double b = p.Horizontal / 100 * MaxTilt * Math.PI / 180;
        double c = p.Rotate * Math.PI / 180;
        var r = Rotation(a, b, c);
        double tz = r[8], tx = r[2] / tz, ty = r[5] / tz;
        double ax = Math.Pow(2, p.Aspect / 200), ay = 1 / ax, s = p.Scale / 100;
        double ox = p.X / 100 * 0.25, oy = p.Y / 100 * 0.25;
        // M = Зсув · Масштаб · Пропорції · (−центр) · R
        double[] t = [ax * s, 0, ox - ax * s * tx, 0, ay * s, oy - ay * s * ty, 0, 0, 1];
        return Multiply(t, r);
    }

    /// <summary>R = Rx(a)·Ry(b)·Rz(c) — те саме для повзунків і для «Авто»-вирівнювання.</summary>
    internal static double[] Rotation(double a, double b, double c)
    {
        double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c), sc = Math.Sin(c);
        double[] rx = [1, 0, 0, 0, ca, -sa, 0, sa, ca];
        double[] ry = [cb, 0, sb, 0, 1, 0, -sb, 0, cb];
        double[] rz = [cc, -sc, 0, sc, cc, 0, 0, 0, 1];
        return Multiply(Multiply(rx, ry), rz);
    }

    internal static double[] Multiply(double[] m, double[] n)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = m[i * 3] * n[j] + m[i * 3 + 1] * n[3 + j] + m[i * 3 + 2] * n[6 + j];
        return r;
    }

    private static void Invert3(double[] m, double[] r)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        double k = 1 / det;
        r[0] = A * k; r[1] = -(b * i - c * h) * k; r[2] = (b * f - c * e) * k;
        r[3] = B * k; r[4] = (a * i - c * g) * k; r[5] = -(a * f - c * d) * k;
        r[6] = C * k; r[7] = -(a * h - b * g) * k; r[8] = (a * e - b * d) * k;
    }

    /// <summary>Точка полотна → точка фото (зелений канал). false — точка «за горизонтом» перспективи.</summary>
    public bool ToSource(double x, double y, out double sx, out double sy)
    {
        // Вирівнювання: поворот у зворотний бік навколо центру.
        double dx = x - _cx, dy = y - _cy;
        double nx = (dx * _cos + dy * _sin) / _f, ny = (-dx * _sin + dy * _cos) / _f;

        double[] m = _inv;
        double w = m[6] * nx + m[7] * ny + m[8];
        if (w <= 1e-6) { sx = sy = double.NaN; return false; }
        double ux = (m[0] * nx + m[1] * ny + m[2]) / w * _f, uy = (m[3] * nx + m[4] * ny + m[5]) / w * _f;

        double factor = 1 + _k * (ux * ux + uy * uy) * _invDiag2;
        ux *= factor;
        uy *= factor;
        if (_profile is { HasDistortion: true } profile)
        {
            // Профіль камери — останнім, найближче до сенсора: він описує саме скло.
            double d = _profileScale * LensProfile.At(profile.Distortion, Math.Sqrt(ux * ux + uy * uy) * _profileScale * _invHalfDiag);
            ux *= d;
            uy *= d;
        }
        sx = _cx + ux;
        sy = _cy + uy;
        return true;
    }

    /// <summary>Точка для червоного чи синього каналу: ручний масштаб каналу × масштаб із профілю на цьому радіусі.</summary>
    public (double X, double Y) Channel(double sx, double sy, bool red)
    {
        double scale = red ? RedScale : BlueScale;
        if (_profile is { HasChromatic: true } profile)
        {
            double r = Math.Sqrt((sx - _cx) * (sx - _cx) + (sy - _cy) * (sy - _cy)) * _invHalfDiag;
            scale *= LensProfile.At(red ? profile.Red : profile.Blue, r);
        }
        return (_cx + (sx - _cx) * scale, _cy + (sy - _cy) * scale);
    }

    /// <summary>Підсилення віньєтування об'єктива в точці фото (у лінійному світлі): профіль × ручний повзунок.</summary>
    public float VignetteGain(double sx, double sy)
    {
        bool fromProfile = _profile is { HasVignette: true };
        if (_vignette == 0 && !fromProfile) return 1;
        double dx = sx - _cx, dy = sy - _cy;
        double r2 = Math.Min(1, (dx * dx + dy * dy) * _invDiag2);
        double gain = _vignette == 0 ? 1 : Math.Pow(2, _vignette * Math.Pow(r2, _vignetteExp));
        if (fromProfile) gain *= LensProfile.At(_profile!.Vignette, Math.Sqrt(r2), extrapolate: false);
        return (float)gain;
    }

    /// <summary>Чи рамка (пікселі полотна) цілком на фото: перевіряємо точки вздовж країв (дисторсія їх вигинає).</summary>
    public bool Covers(double x, double y, double w, double h)
    {
        const double eps = 0.5;
        if (x < -eps || y < -eps || x + w > Width + eps || y + h > Height + eps) return false;
        if (!Params.MovesPixels) return true;
        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            if (!Inside(x + w * t, y) || !Inside(x + w * t, y + h) || !Inside(x, y + h * t) || !Inside(x + w, y + h * t)) return false;
        }
        return true;
    }

    private bool Inside(double x, double y) =>
        ToSource(x, y, out double sx, out double sy) && sx >= -0.5 && sy >= -0.5 && sx <= Width + 0.5 && sy <= Height + 0.5;

    /// <summary>Куди потрапляє центр фото (з урахуванням зсуву й вирівнювання) — довкола нього ставимо рамку.</summary>
    public (double X, double Y) SourceCenterOnCanvas()
    {
        double ox = Params.X / 100 * 0.25 * _f, oy = Params.Y / 100 * 0.25 * _f;
        return (_cx + ox * _cos - oy * _sin, _cy + ox * _sin + oy * _cos);
    }
}
