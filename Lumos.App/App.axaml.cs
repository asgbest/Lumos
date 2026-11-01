using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumos.App.Updates;

namespace Lumos.App;

public partial class App : Application
{
    /// <summary>Скільки щонайменше видно вікно запуску — щоб встигли роздивитися заставку з анімованою діафрагмою.</summary>
    private static readonly TimeSpan SplashMinimum = TimeSpan.FromSeconds(6);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Спершу вікно запуску; головне вікно створюємо, щойно заставка намалювалась (його конструктор важкий).
            // Поки заставка видна — перевіряємо оновлення (лише у зібраному exe з токеном) і, якщо є, ставимо.
            string[] args = desktop.Args ?? [];
            ReleaseUpdater.CleanUpInBackground(args);
            var check = AppUpdate.Updater.FindNewerAsync();
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            var shown = DateTime.UtcNow;
            splash.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(80); // дати заставці відмалюватися до важкої роботи
                if (AppUpdate.Updater.Enabled) splash.Status = "Перевіряю оновлення…";
                if (await AppUpdate.InstallIfNewerAsync(check, splash))
                {
                    desktop.Shutdown();
                    return;
                }
                splash.Status = "Готую робочий простір…";
                var main = new MainWindow(ReleaseUpdater.WithoutUpdateArguments(args).FirstOrDefault());
                desktop.MainWindow = main;
                main.Opened += async (_, _) =>
                {
                    var left = SplashMinimum - (DateTime.UtcNow - shown);
                    if (left > TimeSpan.Zero) await Task.Delay(left);
                    splash.Close();
                    main.Activate();
                    AppUpdate.ShowWhatsNew(args, main);
                    await main.OfferDesktopShortcutAsync();
                };
                main.Show();
            }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
