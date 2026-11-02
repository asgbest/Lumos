using System.Text.Json;

namespace Lumos.App;

/// <summary>Що запам'ятовує інтерфейс між запусками (згорнуті секції тощо). Файл — у профілі користувача: %AppData%\Lumos\ui.json.</summary>
public sealed class UiState
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumos", "ui.json");

    public HashSet<string> CollapsedSections { get; set; } = [];

    /// <summary>Папка й фото, відкриті востаннє, — щоб наступний запуск продовжив з того самого місця.</summary>
    public string? LastFolder { get; set; }
    public string? LastPhoto { get; set; }

    /// <summary>Які групи правок були відмічені в «Копіювати правки» минулого разу (null — усе, крім обрізки).</summary>
    public HashSet<string>? CopyGroups { get; set; }

    /// <summary>Модель Gemini для «✨ AI» (null — типова). Сам ключ — окремо, зашифрований (SecretStore).</summary>
    public string? GeminiModel { get; set; }

    /// <summary>Останнє побажання в «✨ AI».</summary>
    public string? AiWish { get; set; }

    /// <summary>Куди експортувати (null — щоразу питати). Задана — експорт іде туди одразу, без вікна вибору.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>Чи вже питали про ярлик на робочому столі (питаємо раз — під час першого запуску зібраного exe).</summary>
    public bool ShortcutOffered { get; set; }

    public static UiState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException) { } // не критично: просто наступного разу секції будуть розгорнуті
    }
}
