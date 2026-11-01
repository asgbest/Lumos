using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Lumos.App;

public partial class App : Application
{
    /// <summary>Скільки щонайменше видно вікно запуску — щоб не «блимало».</summary>
    private static readonly TimeSpan SplashMinimum = TimeSpan.FromSeconds(1.5);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Спершу вікно запуску; головне вікно створюємо, щойно заставка намалювалась (його конструктор важкий).
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            var shown = DateTime.UtcNow;
            splash.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(80); // дати заставці відмалюватися до важкої роботи
                splash.Status = "Готую робочий простір…";
                var main = new MainWindow(desktop.Args?.FirstOrDefault());
                desktop.MainWindow = main;
                main.Opened += async (_, _) =>
                {
                    var left = SplashMinimum - (DateTime.UtcNow - shown);
                    if (left > TimeSpan.Zero) await Task.Delay(left);
                    splash.Close();
                    main.Activate();
                };
                main.Show();
            }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
