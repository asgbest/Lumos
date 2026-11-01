using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>
/// Ярлик Lumos на робочому столі: під час першого запуску зібраного exe програма пропонує його створити (раз —
/// <see cref="UiState.ShortcutOffered"/>). Ярлик уже є (зокрема перенаправлений автооновленням) — не питаємо.
/// </summary>
public static class DesktopShortcut
{
    /// <summary>Зібраний одним файлом exe (не запуск зі студії чи dotnet run) — лише тоді є що класти на робочий стіл.</summary>
    private static string? Exe =>
        OperatingSystem.IsWindows() && string.IsNullOrEmpty(typeof(DesktopShortcut).Assembly.Location) ? Environment.ProcessPath : null;

    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static async Task OfferAsync(Window owner, UiState ui)
    {
        if (ui.ShortcutOffered || Exe is not { } exe) return;
        ui.ShortcutOffered = true;
        ui.Save();
        if (Exists(exe)) return;
        if (await new OfferDialog().ShowDialog<bool>(owner)) Create(exe);
    }

    private static bool Exists(string exe)
    {
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (string desktop in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                         .Select(Environment.GetFolderPath).Where(Directory.Exists).Distinct())
                foreach (string lnk in Directory.GetFiles(desktop, "*.lnk"))
                {
                    dynamic link = shell.CreateShortcut(lnk);
                    if (string.Equals((string)link.TargetPath, exe, StringComparison.OrdinalIgnoreCase)) return true;
                }
        }
        catch (Exception)
        {
        }
        return false;
    }

    private static void Create(string exe)
    {
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(Path.Combine(Desktop, "Lumos.lnk"));
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Lumos — RAW-редактор фото";
            link.Save();
        }
        catch (Exception)
        {
            // Не вийшло (права, антивірус) — не страшно: програма працює й без ярлика.
        }
    }

    private sealed class OfferDialog : Window
    {
        public OfferDialog()
        {
            Title = "Lumos";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#262626"));

            var create = new Button { Content = "Створити", Classes = { "accent" }, IsDefault = true };
            var skip = new Button { Content = "Не треба", IsCancel = true };
            create.Click += (_, _) => Close(true);
            skip.Click += (_, _) => Close(false);

            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "🖥️ Ярлик на робочому столі", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Створити ярлик Lumos на робочому столі, щоб швидко відкривати програму?",
                        TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")),
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 8, 0, 0), Children = { skip, create },
                    },
                },
            };
        }
    }
}
