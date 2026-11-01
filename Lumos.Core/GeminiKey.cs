namespace Lumos.Core;

/// <summary>
/// Вбудований ключ Gemini API (той самий, що в проєктах School_projects) — «✨ AI» працює одразу, без налаштувань.
/// Сам ключ — у gemini-key.txt поруч (лише на ПК, у git його немає), вбудовується в збірку при збиранні.
/// Немає файлу — ключа немає: тоді «✨ AI» просить ввести свій.
/// </summary>
internal static class GeminiKey
{
    public static string BuiltIn { get; } = Read();

    private static string Read()
    {
        using var stream = typeof(GeminiKey).Assembly.GetManifestResourceStream("Lumos.GeminiKey");
        return stream is null ? "" : new StreamReader(stream).ReadToEnd().Trim();
    }
}
