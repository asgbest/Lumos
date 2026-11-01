using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>Повідомлення, яке не пропустиш (на відміну від рядка стану): заголовок, текст, «Добре».</summary>
public sealed class Notice : Window
{
    private Notice(string title, string text)
    {
        Title = "Lumos";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        var ok = new Button { Content = "Добре", Classes = { "accent" }, IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")) },
                ok,
            },
        };
    }

    public static Task ShowAsync(Window owner, string title, string text) => new Notice(title, text).ShowDialog(owner);
}
