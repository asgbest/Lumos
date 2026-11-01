using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lumos.App.Updates;

/// <summary>«Що нового» — після автооновлення: розділи CHANGELOG.md, новіші за попередню версію.</summary>
public sealed class WhatsNewDialog : Window
{
    public WhatsNewDialog(Version current, IEnumerable<Changelog.Entry> changes)
    {
        Title = "Lumos — що нового";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        var text = new SolidColorBrush(Color.Parse("#D0D0D0"));
        var list = new StackPanel { Spacing = 6 };
        foreach (var entry in changes)
        {
            list.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 8, 0, 0) });
            foreach (string line in entry.Lines)
                list.Children.Add(new SelectableTextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, Foreground = text, Margin = new Thickness(8, 0, 0, 0) });
        }
        var ok = new Button { Content = "Добре", Classes = { "accent" }, IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"✨ Lumos оновлено до версії {current.ToString(3)}", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new ScrollViewer { MaxHeight = 440, Content = list },
                ok,
            },
        };
    }
}
