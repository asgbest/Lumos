using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumos.App.Updates;

/// <summary>
/// Оновлення Lumos під час запуску: поки видно заставку — перевірка, завантаження з прогресом у рядку стану й перезапуск;
/// після оновлення — «Що нового» з CHANGELOG.md. Релізи — у репозиторії <see cref="Repository"/> (викладає tools\Publish-Lumos.ps1).
/// </summary>
public static class AppUpdate
{
    public const string Repository = "asgbest/Lumos";

    public static readonly ReleaseUpdater Updater =
        new("Lumos-v", "Lumos.exe", Assembly.GetExecutingAssembly(), Repository, ReadToken());

    /// <summary>З файлу «update-token.txt», вбудованого при збиранні (у git його немає). Немає — без автооновлення.</summary>
    private static string ReadToken()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lumos.UpdateToken");
        return stream is null ? "" : new StreamReader(stream).ReadToEnd().Trim();
    }

    /// <summary>
    /// Є новіша версія — ставить її, показуючи хід у заставці, і запускає. true — нова версія вже запущена, цю треба закрити.
    /// Не вдалось — причина в заставці кілька секунд, і працюємо на поточній версії (спробуємо під час наступного запуску).
    /// </summary>
    public static async Task<bool> InstallIfNewerAsync(Task<ReleaseUpdater.Update?> check, SplashWindow splash)
    {
        if (await check is not { } update) return false;
        string version = update.Version.ToString(3);
        splash.Status = $"Оновлюю до версії {version}…";
        try
        {
            await Updater.InstallAsync(update, new Progress<double>(share => splash.Status = $"Оновлюю до версії {version}… {share:P0}"));
            splash.Status = "Перезапуск…";
            Updater.Restart();
            return true;
        }
        catch (Exception ex)
        {
            splash.Status = $"Не вдалося оновити: {ex.Message} Відкриваю поточну версію.";
            await Task.Delay(3000);
            return false;
        }
    }

    /// <summary>Щойно оновились — «Що нового» над головним вікном.</summary>
    public static void ShowWhatsNew(string[] args, Window owner)
    {
        if (ReleaseUpdater.UpdatedFrom(args) is not { } from) return;
        var changes = Changelog.Since(Assembly.GetExecutingAssembly(), from, Updater.Current);
        if (changes.Count == 0) return;
        Dispatcher.UIThread.Post(() => new WhatsNewDialog(Updater.Current, changes).ShowDialog(owner));
    }
}
