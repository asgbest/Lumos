using System.Text.Json.Serialization;

namespace Lumos.Core;

/// <summary>Точка тонової кривої: X — вхідна яскравість, Y — вихідна (обидві 0…1).</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// Точкова тонова крива, як у Lightroom. Між точками — монотонний кубічний сплайн (Фріча — Карлсона):
/// крива плавна, але не «перелітає» вище чи нижче сусідніх точок, тож не з'являються зайві горби.
/// Незмінна: кожна правка створює нову криву, тому її безпечно зберігати в історії Ctrl+Z.
/// </summary>
public sealed class Curve : IEquatable<Curve>
{
    public static Curve Identity { get; } = new([new(0, 0), new(1, 1)]);

    public IReadOnlyList<CurvePoint> Points { get; }

    [JsonConstructor]
    public Curve(IReadOnlyList<CurvePoint> points)
    {
        var sorted = points
            .Select(p => new CurvePoint(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)))
            .OrderBy(p => p.X)
            .ToArray();
        Points = sorted.Length >= 2 ? sorted : [new(0, 0), new(1, 1)];
    }

    [JsonIgnore]
    public bool IsIdentity => Points.All(p => Math.Abs(p.X - p.Y) < 1e-9);

    public double Evaluate(double x)
    {
        var p = Points;
        if (x <= p[0].X) return p[0].Y;
        if (x >= p[^1].X) return p[^1].Y;

        double[] m = Tangents();
        int k = 0;
        while (x > p[k + 1].X) k++;
        double h = p[k + 1].X - p[k].X;
        double t = (x - p[k].X) / h, t2 = t * t, t3 = t2 * t;
        double y = (2 * t3 - 3 * t2 + 1) * p[k].Y + (t3 - 2 * t2 + t) * h * m[k]
                 + (-2 * t3 + 3 * t2) * p[k + 1].Y + (t3 - t2) * h * m[k + 1];
        return Math.Clamp(y, 0, 1);
    }

    /// <summary>Значення кривої в <paramref name="size"/> рівномірних точках 0…1 — для швидкої обробки пікселів.</summary>
    public float[] Table(int size)
    {
        var table = new float[size];
        for (int i = 0; i < size; i++) table[i] = (float)Evaluate(i / (double)(size - 1));
        return table;
    }

    private double[] Tangents()
    {
        var p = Points;
        int n = p.Count;
        var d = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
            d[k] = (p[k + 1].Y - p[k].Y) / Math.Max(1e-9, p[k + 1].X - p[k].X);

        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int k = 1; k < n - 1; k++)
            m[k] = d[k - 1] * d[k] <= 0 ? 0 : (d[k - 1] + d[k]) / 2;

        for (int k = 0; k < n - 1; k++)
        {
            if (d[k] == 0) { m[k] = m[k + 1] = 0; continue; }
            double a = m[k] / d[k], b = m[k + 1] / d[k], s = a * a + b * b;
            if (s > 9)
            {
                double t = 3 / Math.Sqrt(s);
                m[k] = t * a * d[k];
                m[k + 1] = t * b * d[k];
            }
        }
        return m;
    }

    public bool Equals(Curve? other) => other is not null && Points.SequenceEqual(other.Points);
    public override bool Equals(object? obj) => Equals(obj as Curve);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in Points) hash.Add(p);
        return hash.ToHashCode();
    }
}
