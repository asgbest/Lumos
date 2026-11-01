using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>Маленьке вікно «введи назву» (Enter — гаразд, Esc — скасувати). Повертає назву або null.</summary>
public sealed class NameDialog : Window
{
    private readonly TextBox _input;

    public NameDialog(string title, string prompt, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        _input = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Зберегти", Classes = { "accent" }, IsDefault = true };
        var cancel = new Button { Content = "Скасувати", IsCancel = true };
        ok.Click += (_, _) => Finish();
        cancel.Click += (_, _) => Close(null);
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(); e.Handled = true; }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { new TextBlock { Text = prompt, Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")) }, _input, buttons },
        };
        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void Finish()
    {
        string text = (_input.Text ?? "").Trim();
        if (text.Length > 0) Close(text);
    }
}
