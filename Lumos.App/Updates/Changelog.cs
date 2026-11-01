using System.Reflection;
using System.Text.RegularExpressions;

namespace Lumos.App.Updates;

/// <summary>
/// Історія версій — CHANGELOG.md, вбудований у exe при збиранні («Lumos.Changelog»). Після автооновлення програма
/// показує розділи «## X.Y.Z — дата», новіші за версію, з якої оновились.
/// </summary>
public static class Changelog
{
    public sealed record Entry(Version Version, string Title, List<string> Lines);

    public static List<Entry> Read(Assembly app)
    {
        var entries = new List<Entry>();
        using var stream = app.GetManifestResourceStream("Lumos.Changelog");
        if (stream is null) return entries;
        using var reader = new StreamReader(stream);

        Entry? current = null;
        while (reader.ReadLine() is { } line)
        {
            var header = Regex.Match(line, @"^##\s+(\d+\.\d+\.\d+)(.*)$");
            if (header.Success && Version.TryParse(header.Groups[1].Value, out var version))
            {
                current = new Entry(version, line.TrimStart('#', ' '), []);
                entries.Add(current);
            }
            else if (current is not null && line.Trim().Length > 0)
            {
                current.Lines.Add(line.Trim().TrimStart('-', '*', ' '));
            }
        }
        return entries;
    }

    /// <summary>Зміни після версії <paramref name="from"/> до <paramref name="current"/> включно, новіші першими.</summary>
    public static List<Entry> Since(Assembly app, Version from, Version current) =>
        Read(app).Where(e => e.Version > from && e.Version <= current).OrderByDescending(e => e.Version).ToList();
}
