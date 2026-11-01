using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Lumos.App;

/// <summary>
/// Вікно запуску, як у програм Adobe: без рамки, ліворуч — назва, версія й подяки, праворуч — малюнок «Люмос!»
/// з діафрагмою, що дихає (<see cref="SplashArt"/>). Показується, поки готується головне вікно.
/// </summary>
public sealed class SplashWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#14122B"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#5C5A70"));

    private readonly TextBlock _status = new() { Text = "Запуск…", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    public SplashWindow()
    {
        Width = 760;
        Height = 460;
        CanResize = false;
        Topmost = true; // головне вікно відкривається під заставкою, а не поверх неї
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Lumos";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Lumos/Assets/lumos.ico")));

        string version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

        var left = new DockPanel { Margin = new Thickness(34, 30, 28, 24) };
        var top = new StackPanel { Spacing = 2, [DockPanel.DockProperty] = Dock.Top };
        top.Children.Add(new TextBlock
        {
            Text = "Lumos", FontSize = 44, FontWeight = FontWeight.Bold, LetterSpacing = -1,
            Foreground = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#3B2F9E"), 0), new GradientStop(Color.Parse("#C8892E"), 1) },
            },
        });
        top.Children.Add(new TextBlock { Text = "RAW-редактор фото", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ink });
        left.Children.Add(top);

        var bottom = new StackPanel { Spacing = 10, [DockPanel.DockProperty] = Dock.Bottom };
        bottom.Children.Add(new TextBlock
        {
            Text = $"Версія {version}\n© 2026 Lumos. Усі права захищено.",
            FontSize = 12, Foreground = Ink, LineHeight = 17,
        });
        bottom.Children.Add(new TextBlock
        {
            Text = "LibRaw, Avalonia, ImageMagick, SkiaSharp, ONNX Runtime, ComputeSharp, SQLite; моделі IS-Net (Xuebin Qin та ін.), " +
                   "U²-Net skyseg, UperNet ConvNeXt (OpenMMLab), LaMa (Samsung AI Center), Depth Anything V2 (Lihe Yang та ін.), " +
                   "Gemini (Google). Дякуємо їхнім авторам.",
            FontSize = 9.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 13,
        });
        bottom.Children.Add(_status);
        left.Children.Add(bottom);
        left.Children.Add(new Control()); // заповнювач посередині

        // Малюнок з анімованою діафрагмою; праві кути заокруглює сам.
        var art = new SplashArt { [Grid.ColumnProperty] = 1 };

        Content = new Grid
        {
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(12),
                    ClipToBounds = true,
                    Background = Brushes.White,
                    Child = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), Children = { left, art } },
                },
                // Тонкий кант поверх — однаковий по всьому краю.
                new Border
                {
                    CornerRadius = new CornerRadius(12),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                },
            },
        };
    }

    /// <summary>Що зараз робиться (рядок унизу ліворуч).</summary>
    public string Status
    {
        set => _status.Text = value;
    }
}
