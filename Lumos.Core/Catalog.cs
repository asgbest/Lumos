using Microsoft.Data.Sqlite;

namespace Lumos.Core;

/// <summary>Позначка фото: вибране (P), відхилене (X) або без позначки — як прапорці в Lightroom.</summary>
public enum PhotoFlag { None = 0, Pick = 1, Reject = -1 }

/// <summary>
/// Каталог бібліотеки: рейтинги й позначки фото. Один файл SQLite у профілі (%AppData%\Lumos\catalog.db) —
/// фото й папки користувача не змінюються. Шлях — ключ (без урахування регістру, як у Windows).
/// Пізніше сюди ж ляжуть ключові слова й альбоми.
/// </summary>
public sealed class Catalog : IDisposable
{
    private readonly SqliteConnection _db;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumos", "catalog.db");

    public Catalog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Execute("""
            CREATE TABLE IF NOT EXISTS photos (
                path   TEXT PRIMARY KEY COLLATE NOCASE,
                rating INTEGER NOT NULL DEFAULT 0,
                flag   INTEGER NOT NULL DEFAULT 0
            )
            """);
    }

    /// <summary>Рейтинги й позначки всіх фото з папки (без підпапок).</summary>
    public Dictionary<string, (int Rating, PhotoFlag Flag)> LoadFolder(string folder)
    {
        var result = new Dictionary<string, (int, PhotoFlag)>(StringComparer.OrdinalIgnoreCase);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, rating, flag FROM photos WHERE path LIKE $prefix ESCAPE '\\'";
        cmd.Parameters.AddWithValue("$prefix", prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetInt32(1), (PhotoFlag)reader.GetInt32(2));
        return result;
    }

    public void Set(string path, int rating, PhotoFlag flag)
    {
        using var cmd = _db.CreateCommand();
        if (rating == 0 && flag == PhotoFlag.None)
        {
            cmd.CommandText = "DELETE FROM photos WHERE path = $path";
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO photos (path, rating, flag) VALUES ($path, $rating, $flag)
                ON CONFLICT(path) DO UPDATE SET rating = excluded.rating, flag = excluded.flag
                """;
            cmd.Parameters.AddWithValue("$rating", rating);
            cmd.Parameters.AddWithValue("$flag", (int)flag);
        }
        cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Фото переїхало в іншу папку — рейтинг і позначка йдуть за ним.</summary>
    public void Rename(string oldPath, string newPath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM photos WHERE path = $new;
            UPDATE photos SET path = $new WHERE path = $old;
            """;
        cmd.Parameters.AddWithValue("$old", Path.GetFullPath(oldPath));
        cmd.Parameters.AddWithValue("$new", Path.GetFullPath(newPath));
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
