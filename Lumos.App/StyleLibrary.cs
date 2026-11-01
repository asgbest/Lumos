using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Бібліотека стилів Lumos: %AppData%\Lumos\Styles\*.lumosstyle. Імпортовані пресети Lightroom
/// зберігаються тут уже переведеними, тож оригінальні файли після імпорту не потрібні.
/// Стиль із тією ж назвою й групою перезаписується — повторний імпорт оновлює, а не дублює.
/// </summary>
public static class StyleLibrary
{
    public const string OwnGroup = "Мої стилі";

    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumos", "Styles");

    public sealed record Entry(Style Style, string FilePath);

    public static List<Entry> Load()
    {
        var entries = new List<Entry>();
        if (!Directory.Exists(Folder)) return entries;
        foreach (var file in Directory.GetFiles(Folder, "*" + StylePresets.Extension))
        {
            var (styles, _) = StylePresets.Import(file);
            if (styles.Count > 0) entries.Add(new Entry(styles[0], file));
        }
        return entries;
    }

    public static void Add(Style style)
    {
        Directory.CreateDirectory(Folder);
        StylePresets.Save(style, Path.Combine(Folder, FileName(style)));
    }

    public static void Delete(Entry entry) => File.Delete(entry.FilePath);

    private static string FileName(Style style)
    {
        string name = style.Group.Length > 0 ? $"{style.Group} — {style.Name}" : style.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim() + StylePresets.Extension;
    }
}
