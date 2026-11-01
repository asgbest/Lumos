using System.Collections.Concurrent;

namespace Lumos.Core;

/// <summary>
/// Профіль об'єктива, який камера сама записала в RAW (Sony ARW: теги 0x7032 — віньєтування, 0x7035 — хроматичні аберації,
/// 0x7037 — дисторсія, по 16 точок від центру до кута кадру). Так виправляє і сама камера у своїх JPEG, і Lightroom
/// для об'єктивів Sony — без бази профілів. Числа вже перераховані в множники:
/// <list type="bullet">
/// <item>дисторсія — у скільки разів далі від центру брати точку фото (1 — без змін);</item>
/// <item>червоний і синій — додатковий радіальний масштаб каналу;</item>
/// <item>віньєтування — підсилення яскравості (у кутах більше за 1).</item>
/// </list>
/// Радіус — відстань від центру в частках половини діагоналі. Формули — як у darktable (lens.cc, «Sony»).
/// </summary>
public sealed record LensProfile(EquatableList<double> Distortion, EquatableList<double> Red, EquatableList<double> Blue,
    EquatableList<double> Vignette)
{
    public bool HasDistortion => Distortion.Count > 1;
    public bool HasChromatic => Red.Count > 1;
    public bool HasVignette => Vignette.Count > 1;

    /// <summary>
    /// Значення таблиці на радіусі r (0 — центр, 1 — кут). Точка i лежить на радіусі (i + 0.5) / (n − 1), як у Sony
    /// (і в darktable); між точками — лінійно, за крайніми — продовження крайнього відрізка (або саме крайнє значення).
    /// </summary>
    public static double At(EquatableList<double> table, double r, bool extrapolate = true)
    {
        int n = table.Count;
        if (n == 0) return 1;
        if (n == 1) return table[0];
        double t = Math.Max(0, r) * (n - 1) - 0.5;
        if (!extrapolate) t = Math.Clamp(t, 0, n - 1);
        int i = Math.Clamp((int)Math.Floor(t), 0, n - 2);
        return table[i] + (table[i + 1] - table[i]) * (t - i);
    }

    /// <summary>Що виправляє профіль — для підказки в панелі.</summary>
    public string Describe() =>
        string.Join(", ", new[] { (HasDistortion, "дисторсія"), (HasChromatic, "кольорова облямівка"), (HasVignette, "віньєтування") }
            .Where(p => p.Item1).Select(p => p.Item2));
}

/// <summary>
/// Читає <see cref="LensProfile"/> із RAW (лише службові теги TIFF, без розкодування зображення — це мілісекунди)
/// і пам'ятає результат для кожного файлу.
/// </summary>
public static class LensProfiles
{
    private static readonly ConcurrentDictionary<string, LensProfile?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static LensProfile? For(string photoPath)
    {
        if (photoPath.Length == 0 || PhotoDecoder.IsRendered(photoPath)) return null;
        return Cache.GetOrAdd(photoPath, path =>
        {
            try
            {
                return ReadSony(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Числа профілю цього фото — у правки (діють, лише коли «Профіль об'єктива» увімкнено).
    /// У файл правок вони не пишуться (вони завжди є в RAW), тож їх «чіпляють» після завантаження.
    /// </summary>
    public static void Attach(DevelopSettings s, string photoPath) => s.LensProfileData = For(photoPath);

    private static LensProfile? ReadSony(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        using var reader = new BinaryReader(file);
        var order = reader.ReadUInt16();
        if (order != 0x4949) return null; // ARW — завжди little-endian TIFF
        if (reader.ReadUInt16() != 42) return null;

        short[]? vignette = null, chromatic = null, distortion = null;
        var seen = new HashSet<long>();
        var queue = new Queue<long>([reader.ReadUInt32()]);
        while (queue.Count > 0 && seen.Count < 16)
        {
            long offset = queue.Dequeue();
            if (offset <= 0 || offset > file.Length - 2 || !seen.Add(offset)) continue;
            file.Position = offset;
            int count = reader.ReadUInt16();
            if (count > 1000) continue;
            var entries = new (ushort Tag, ushort Type, uint Count, uint Value)[count];
            for (int i = 0; i < count; i++)
                entries[i] = (reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32());
            queue.Enqueue(reader.ReadUInt32()); // наступний IFD

            foreach (var (tag, type, n, value) in entries)
            {
                if (tag == 0x014A) // SubIFDs: тут Sony тримає параметри об'єктива
                {
                    if (n == 1) queue.Enqueue(value);
                    else if (n < 16)
                    {
                        file.Position = value;
                        for (int k = 0; k < n; k++) queue.Enqueue(reader.ReadUInt32());
                    }
                }
                else if (tag is 0x7032 or 0x7035 or 0x7037 && type == 8 && n is > 2 and <= 64) // int16, дані — за адресою в полі
                {
                    file.Position = value;
                    var values = new short[n];
                    for (int k = 0; k < n; k++) values[k] = reader.ReadInt16();
                    if (tag == 0x7032) vignette = values;
                    else if (tag == 0x7035) chromatic = values;
                    else distortion = values;
                }
            }
        }

        // Перше число — скільки точок; далі самі точки (для аберацій — спершу всі червоні, потім усі сині).
        var dist = Table(distortion, v => v * Math.Pow(2, -14) + 1);
        var vig = Table(vignette, v => 1 / Math.Pow(2, 0.5 - Math.Pow(2, v * Math.Pow(2, -13) - 1)));
        EquatableList<double> red = EquatableList<double>.Empty, blue = EquatableList<double>.Empty;
        if (chromatic is { Length: > 2 } ca && ca[0] == ca.Length - 1 && ca[0] % 2 == 0 && ca[0] <= 32)
        {
            int half = ca[0] / 2;
            red = new(ca.Skip(1).Take(half).Select(v => v * Math.Pow(2, -21) + 1));
            blue = new(ca.Skip(1 + half).Take(half).Select(v => v * Math.Pow(2, -21) + 1));
        }
        var profile = new LensProfile(dist, red, blue, vig);
        return profile.HasDistortion || profile.HasChromatic || profile.HasVignette ? profile : null;
    }

    private static EquatableList<double> Table(short[]? values, Func<double, double> convert) =>
        values is { Length: > 2 } v && v[0] == v.Length - 1 && v[0] <= 32
            ? new(v.Skip(1).Select(x => convert(x)))
            : EquatableList<double>.Empty;
}
