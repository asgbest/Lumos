using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>Повідомлення, яке не пропустиш (на відміну від рядка стану): заголовок, текст, «Добре» — або питання з «Так / Скасувати».</summary>
public sealed class Notice : Window
{
    private Notice(string title, string text, string? yes)
    {
        Title = "Lumos";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (yes is null)
        {
            var ok = new Button { Content = "Добре", Classes = { "accent" }, IsDefault = true, IsCancel = true, MinWidth = 90 };
            ok.Click += (_, _) => Close();
            buttons.Children.Add(ok);
        }
        else
        {
            var cancel = new Button { Content = "Скасувати", IsCancel = true, MinWidth = 90 };
            cancel.Click += (_, _) => Close(false);
            var confirm = new Button { Content = yes, Classes = { "accent" }, IsDefault = true, MinWidth = 90 };
            confirm.Click += (_, _) => Close(true);
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")) },
                buttons,
            },
        };
    }

    public static Task ShowAsync(Window owner, string title, string text) => new Notice(title, text, null).ShowDialog(owner);

    /// <returns>true — натиснули <paramref name="yes"/>; false — «Скасувати» чи закрили вікно.</returns>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string text, string yes) =>
        await new Notice(title, text, yes).ShowDialog<bool?>(owner) == true;
}
