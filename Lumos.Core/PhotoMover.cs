namespace Lumos.Core;

/// <summary>
/// Переміщення фото в іншу папку разом із супутніми файлами: правками Lumos (.lumos.json) і XMP від Lightroom / Camera Raw.
/// Рейтинг у каталозі й варіанти AI переносить той, хто викликає (вони не поруч із фото).
/// </summary>
public static class PhotoMover
{
    /// <summary>Підпапка для відібраних (⚑) фото.</summary>
    public const string PicksFolder = "Відібрані";

    /// <returns>Новий шлях фото.</returns>
    /// <exception cref="IOException">У папці вже є файл з такою назвою, або фото зайняте іншою програмою.</exception>
    public static string Move(string photoPath, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        string target = Path.Combine(targetFolder, Path.GetFileName(photoPath));
        if (File.Exists(target)) throw new IOException($"у «{Path.GetFileName(targetFolder)}» вже є {Path.GetFileName(target)}");

        // Фото може ще читатись у фоні (мініатюра, повна роздільність) або його тримає OneDrive — кілька спроб.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(photoPath, target);
                break;
            }
            catch (IOException) when (attempt < 8 && File.Exists(photoPath) && !File.Exists(target))
            {
                Thread.Sleep(250);
            }
        }

        foreach (var companion in Companions(photoPath))
        {
            string to = Path.Combine(targetFolder, Path.GetFileName(companion));
            if (File.Exists(companion) && !File.Exists(to)) File.Move(companion, to);
        }
        return target;
    }

    /// <summary>Файли, що належать саме цьому фото: правки Lumos, «DSC.ARW.xmp», а для RAW — ще й «DSC.xmp».</summary>
    private static IEnumerable<string> Companions(string photoPath)
    {
        foreach (var file in EditSidecar.Files(photoPath)) yield return file;
        yield return photoPath + ".xmp";
        if (!PhotoDecoder.IsRendered(photoPath)) yield return Path.ChangeExtension(photoPath, ".xmp");
    }
}
