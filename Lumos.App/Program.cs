using Avalonia;
using Lumos.Core;

namespace Lumos.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        MigrateFromLumen();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// Програма раніше звалася Lumen: її налаштування, стилі й каталог переносимо в папки Lumos один раз
    /// (позначка «.migrated»): старі файли копіюються поверх нових, стилі .lumenstyle стають .lumosstyle,
    /// і лише після вдалого копіювання стара папка прибирається. Не вдалось — спробуємо наступного запуску.
    /// </summary>
    private static void MigrateFromLumen()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string baseDir = Environment.GetFolderPath(root);
            string oldDir = Path.Combine(baseDir, "Lumen"), newDir = Path.Combine(baseDir, "Lumos");
            string marker = Path.Combine(newDir, ".migrated");
            if (!Directory.Exists(oldDir) || File.Exists(marker)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(oldDir, file);
                    if (relative.EndsWith(".lumenstyle", StringComparison.OrdinalIgnoreCase))
                        relative = Path.ChangeExtension(relative, StylePresets.Extension);
                    string target = Path.Combine(newDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
                File.WriteAllText(marker, "Перенесено з Lumen");
                Directory.Delete(oldDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
