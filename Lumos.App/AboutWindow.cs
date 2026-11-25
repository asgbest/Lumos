using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lumos.Core;

namespace Lumos.App;

/// <summary>«Про програму»: що це, версія, автор, нейромережі й бібліотеки з ліцензіями та посиланнями.</summary>
public sealed class AboutWindow : Window
{
    /// <summary>Хто зробив програму (порожньо — рядок не показується).</summary>
    public const string Author = "Лось Софія";

    private static readonly (string Name, string Purpose, string License)[] Libraries =
    [
        ("Avalonia", "інтерфейс", "MIT"),
        ("LibRaw (Sdcb.LibRaw)", "розкодування RAW", "LGPL-2.1 / CDDL-1.0"),
        ("Magick.NET / ImageMagick", "JPEG, PNG, TIFF, HEIC, WebP, EXIF", "Apache-2.0"),
        ("SkiaSharp", "кодування JPEG", "MIT"),
        ("ONNX Runtime (DirectML)", "нейромережі на відеокарті", "MIT"),
        ("ComputeSharp", "обробка фото на відеокарті (DirectX 12)", "MIT"),
        ("Microsoft.Data.Sqlite", "каталог бібліотеки", "MIT"),
    ];

    public AboutWindow()
    {
        Title = "Про програму";
        Width = 560;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        IBrush dim = new SolidColorBrush(Color.Parse("#A0A0A0")), text = new SolidColorBrush(Color.Parse("#D8D8D8"));
        var page = new StackPanel { Margin = new Thickness(24, 20), Spacing = 6 };

        page.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Children =
            {
                new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Lumos/Assets/lumos.ico"))), Width = 56, Height = 56 },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Lumos", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = text },
                        new TextBlock { Text = $"Версія {(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0)).ToString(3)}", Foreground = dim },
                    },
                },
            },
        });
        page.Children.Add(Paragraph("Редактор фотографій і RAW-файлів з ШІ-масками, ШІ-видаленням і ШІ-стилістом. " +
                                    "Оригінали не змінюються: правки зберігаються поруч з фото, а експорт — у повній роздільності й найвищій якості.", text));
        if (Author.Length > 0) page.Children.Add(Paragraph($"Автор: {Author}", text));

        page.Children.Add(Heading("Нейромережі"));
        foreach (var model in new[] { AiModels.Sky, AiModels.Subject, AiModels.Landscape, AiModels.Inpaint, AiModels.Depth })
        {
            page.Children.Add(Paragraph($"{model.Title} — {model.Authors}. Ліцензія {model.License}.", text));
            if (model.Url is { } url) page.Children.Add(Link(url));
        }

        page.Children.Add(Heading("Бібліотеки"));
        foreach (var (name, purpose, license) in Libraries)
            page.Children.Add(Paragraph($"{name} — {purpose} ({license})", text));
        page.Children.Add(Paragraph("Дякуємо авторам усіх цих проєктів.", dim));

        var close = new Button { Content = "Закрити", Classes = { "accent" }, IsDefault = true, IsCancel = true, MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 10) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        Content = new DockPanel { Children = { close, new ScrollViewer { Content = page } } };
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2) };

    private static SelectableTextBlock Paragraph(string text, IBrush brush) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush, FontSize = 13 };

    private static TextBlock Link(string url)
    {
        var link = new TextBlock
        {
            Text = url, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9C95FF")), TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TextWrapping = TextWrapping.Wrap,
        };
        link.PointerPressed += (_, _) => HelpWindow.Open(url);
        return link;
    }
}
