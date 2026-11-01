using System.Text.Json;

namespace Lumos.Core;

/// <summary>
/// Правки зберігаються поруч із фото: <c>DSC09002.ARW.lumos.json</c>. Сам RAW ніколи не змінюється.
/// Файл переїжджає разом із фото, і його можна просто видалити, щоб повернути оригінал.
/// </summary>
public static class EditSidecar
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string photoPath) => photoPath + ".lumos.json";

    /// <summary>Так файли правок називались, поки програма звалася Lumen: читаємо їх, а при збереженні переводимо на нову назву.</summary>
    private static string LegacyPathFor(string photoPath) => photoPath + ".lumen.json";

    /// <summary>Правки фото або стартові (<see cref="DevelopSettings.DefaultFor"/>), якщо їх ще немає чи файл пошкоджено.</summary>
    public static DevelopSettings Load(string photoPath)
    {
        try
        {
            string path = File.Exists(PathFor(photoPath)) ? PathFor(photoPath) : LegacyPathFor(photoPath);
            if (File.Exists(path) && JsonSerializer.Deserialize<DevelopSettings>(File.ReadAllText(path), Json) is { } saved)
            {
                LensProfiles.Attach(saved, photoPath);
                return saved;
            }
        }
        catch (JsonException) { }
        return DevelopSettings.DefaultFor(photoPath);
    }

    public static bool Exists(string photoPath) => File.Exists(PathFor(photoPath)) || File.Exists(LegacyPathFor(photoPath));

    /// <summary>Коли файл правок востаннє змінювався (для кешу мініатюр); null — правок немає.</summary>
    public static DateTime? LastWrite(string photoPath)
    {
        foreach (var path in new[] { PathFor(photoPath), LegacyPathFor(photoPath) })
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
        return null;
    }

    /// <summary>Записує правки; якщо лишились тільки стартові — прибирає файл, щоб не смітити.</summary>
    public static void Save(string photoPath, DevelopSettings settings)
    {
        string path = PathFor(photoPath);
        File.Delete(LegacyPathFor(photoPath)); // старий файл (з часів Lumen) замінюється новим
        if (settings.IsDefaultFor(photoPath))
        {
            File.Delete(path);
            return;
        }
        // Спершу в тимчасовий файл: якщо запис обірветься, старі правки не пропадуть.
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true);
    }
}
