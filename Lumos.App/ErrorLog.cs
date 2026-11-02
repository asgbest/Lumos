namespace Lumos.App;

/// <summary>
/// Журнал помилок: %LocalAppData%\Lumos\errors.log — повний текст винятку (на екрані — лише зрозуміле пояснення).
/// Найновіші — внизу; файл обрізається, коли перевищить ~1 МБ.
/// </summary>
public static class ErrorLog
{
    public static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumos", "errors.log");

    public static void Write(string what, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
