using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Lumos.App.Updates;

/// <summary>
/// Автооновлення з релізів GitHub (як у шкільних програмах, AppUpdates): тег «Lumos-v1.2.3», файл Lumos.exe — їх викладає
/// tools\Publish-Lumos.ps1. Під час запуску програма шукає новішу версію, завантажує її поруч, підміняє свій exe (запущений
/// файл Windows дозволяє перейменувати) і перезапускається з «--updated {pid} {стара версія}»; стару копію прибирає нова.
/// </summary>
public sealed class ReleaseUpdater
{
    public const string UpdatedArgument = "--updated";

    public sealed record Update(Version Version, long AssetId, long Size);

    private readonly string _tagPrefix;
    private readonly string _assetName;
    private readonly string _releases;
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly bool _bundled;

    /// <param name="tagPrefix">«Lumos-v».</param>
    /// <param name="assetName">«Lumos.exe».</param>
    /// <param name="app">Збірка програми — її версія (&lt;Version&gt; у csproj) і є поточною.</param>
    /// <param name="repository">«власник/репозиторій» з релізами.</param>
    /// <param name="token">Токен GitHub з доступом до релізів цього репозиторію; порожній — оновлення вимкнено.</param>
    public ReleaseUpdater(string tagPrefix, string assetName, Assembly app, string repository, string token)
    {
        _tagPrefix = tagPrefix;
        _assetName = assetName;
        _token = token;
        _releases = $"https://api.github.com/repos/{repository}/releases";
        Current = app.GetName().Version ?? new Version(0, 0);
        _bundled = string.IsNullOrEmpty(app.Location);

        // Завантаження з GitHub перенаправляється на сховище файлів; заголовок авторизації туди не передається.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lumos-Updater");
        if (token.Length > 0) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public Version Current { get; }

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>Лише зібраний одним файлом exe (не запуск зі студії чи dotnet run) і з токеном.</summary>
    public bool Enabled => _bundled && !Debugger.IsAttached && _token.Length > 0 && ExePath != null;

    /// <summary>Новіша версія, ніж запущена; null — немає, немає інтернету чи GitHub не відповів.</summary>
    public async Task<Update?> FindNewerAsync()
    {
        if (!Enabled) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            // GitHub сортує релізи за датою коміту, а не за версією, — новий може бути не на першій сторінці.
            var found = new List<JsonNode?>();
            for (int page = 1; page <= 10; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_releases}?per_page=100&page={page}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await _http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                var batch = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))?.AsArray() ?? [];
                found.AddRange(batch);
                if (batch.Count < 100) break;
            }

            Update? best = null;
            foreach (var release in found)
            {
                string tag = (string?)release?["tag_name"] ?? "";
                if (release is null || (bool?)release["draft"] == true || !tag.StartsWith(_tagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Version.TryParse(tag[_tagPrefix.Length..], out var version) || version <= Current) continue;
                if (best is not null && version <= best.Version) continue;

                var asset = release["assets"]?.AsArray().FirstOrDefault(a => string.Equals((string?)a?["name"], _assetName, StringComparison.OrdinalIgnoreCase));
                if (asset is null) continue;
                best = new Update(version, (long?)asset["id"] ?? 0, (long?)asset["size"] ?? 0);
            }
            return best is { AssetId: > 0 } ? best : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Куди поставлено нову версію, якщо не поруч з exe (Restart запустить її звідти); null — підмінено на місці.</summary>
    public string? InstalledTo { get; private set; }

    /// <summary>
    /// Своя папка користувача (%LocalAppData%\Programs\Lumos\Lumos.exe) — туди ставимо оновлення, якщо поруч з exe писати
    /// не можна (exe лежить у Program Files чи в корені диска без прав адміністратора).
    /// </summary>
    private string UserCopy => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", Path.GetFileNameWithoutExtension(_assetName), _assetName);

    /// <summary>Завантажує нову версію поруч з exe і підміняє його; після цього лишається запустити її (Restart).</summary>
    public async Task InstallAsync(Update update, IProgress<double> progress)
    {
        string exe = ExePath!;
        if (!CanWriteNextTo(exe))
        {
            await InstallToUserCopyAsync(update, progress, exe);
            return;
        }
        try
        {
            await DownloadAndReplaceAsync(update, progress, exe);
        }
        catch (UnauthorizedAccessException)
        {
            // Папка дозволяє писати, але сам exe Windows не дає змінити (антивірус, права на файл) — у свою папку користувача.
            await InstallToUserCopyAsync(update, progress, exe);
        }
    }

    private static bool CanWriteNextTo(string exe)
    {
        string probe = exe + ".probe";
        try
        {
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Поруч з exe писати не можна — нова версія у своїй папці користувача; ярлики на робочому столі, що вели на старий
    /// exe, — на неї. Там уже є така чи новіша версія — нічого не завантажуємо.
    /// </summary>
    private async Task InstallToUserCopyAsync(Update update, IProgress<double> progress, string oldExe)
    {
        string copy = UserCopy;
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (!File.Exists(copy) || VersionOf(copy) is not { } have || have < update.Version)
            await DownloadAndReplaceAsync(update, progress, copy);
        InstalledTo = copy;
        RetargetDesktopShortcuts(oldExe, copy);
    }

    private static Version? VersionOf(string exe)
    {
        try
        {
            string? text = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
            return Version.TryParse(text, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Ярлики (.lnk) на робочому столі, що ведуть на старий exe, — на новий.</summary>
    private static void RetargetDesktopShortcuts(string oldExe, string newExe)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (string desktop in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                         .Select(Environment.GetFolderPath).Where(Directory.Exists).Distinct())
                foreach (string lnk in Directory.GetFiles(desktop, "*.lnk"))
                {
                    try
                    {
                        dynamic link = shell.CreateShortcut(lnk);
                        if (!string.Equals((string)link.TargetPath, oldExe, StringComparison.OrdinalIgnoreCase)) continue;
                        link.TargetPath = newExe;
                        link.WorkingDirectory = Path.GetDirectoryName(newExe);
                        link.IconLocation = newExe + ",0";
                        link.Save();
                    }
                    catch (Exception)
                    {
                        // Спільний ярлик без прав на зміну — лишається як є: стара копія сама перекине на нову.
                    }
                }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Завантажує нову версію поруч з <paramref name="exe"/> («.new») і ставить на його місце.</summary>
    private async Task DownloadAndReplaceAsync(Update update, IProgress<double> progress, string exe)
    {
        string downloaded = exe + ".new";
        string old = exe + ".old";
        // Лишився «.new» від минулої спроби, який не видаляється (заблокований, «лише для читання»), — інше ім'я.
        try
        {
            if (File.Exists(downloaded))
            {
                File.SetAttributes(downloaded, FileAttributes.Normal);
                File.Delete(downloaded);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            downloaded = $"{exe}.{update.Version.ToString(3)}.new";
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{_releases}/assets/{update.AssetId}"))
        {
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = new FileStream(downloaded, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        // Обірване завантаження не має замінити робочу програму.
        var info = new FileInfo(downloaded);
        if (update.Size > 0 && info.Length != update.Size || !StartsWithMz(downloaded))
        {
            File.Delete(downloaded);
            throw new IOException("Файл оновлення завантажився не повністю.");
        }

        if (!File.Exists(exe))
        {
            File.Move(downloaded, exe); // перша копія у своїй папці користувача
            return;
        }
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    /// <summary>Запускає оновлену програму; вона прибере стару копію, коли ця закриється.</summary>
    public void Restart()
    {
        string exe = InstalledTo ?? ExePath!;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add(UpdatedArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(Current.ToString(3));
        Process.Start(start);
    }

    /// <summary>Версія, з якої щойно оновились (аргумент від старої програми); null — звичайний запуск.</summary>
    public static Version? UpdatedFrom(string[] args) =>
        args.Length >= 3 && args[0] == UpdatedArgument && Version.TryParse(args[2], out var version) ? version : null;

    /// <summary>Аргументи без «--updated {pid} {версія}» — те, що лишається програмі (наприклад, файл для відкриття).</summary>
    public static string[] WithoutUpdateArguments(string[] args) =>
        args.Length >= 1 && args[0] == UpdatedArgument ? args.Skip(3).ToArray() : args;

    /// <summary>Видаляє стару версію й недокачані файли — у фоні, чекаючи, поки стара програма закриється.</summary>
    public static void CleanUpInBackground(string[] args)
    {
        if (ExePath is null) return;
        var leftovers = new List<string> { ExePath + ".old", ExePath + ".new" };
        try
        {
            // «Lumos.exe.0.1.2.new» — коли звичайний «.new» не видалявся.
            leftovers.AddRange(Directory.GetFiles(Path.GetDirectoryName(ExePath)!, Path.GetFileName(ExePath) + ".*.new"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
        int oldProcess = args.Length >= 2 && args[0] == UpdatedArgument && int.TryParse(args[1], out int id) ? id : 0;
        Task.Run(async () =>
        {
            if (oldProcess > 0)
                try
                {
                    using var process = Process.GetProcessById(oldProcess);
                    process.WaitForExit(30000);
                }
                catch (Exception)
                {
                    // Уже закрилась.
                }
            for (int attempt = 0; attempt < 15; attempt++)
            {
                bool left = false;
                foreach (string file in leftovers)
                {
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                    }
                    catch (Exception)
                    {
                        left = true;
                    }
                }
                if (!left) return;
                await Task.Delay(1000);
            }
        });
    }

    private static bool StartsWithMz(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }
}
