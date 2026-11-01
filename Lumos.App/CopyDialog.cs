using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// «Що копіювати?» — галочки груп правок, як у Lightroom. Повертає вибрані групи або null (скасовано).
/// Обрізка за замовчуванням вимкнена: у різних кадрів вона зазвичай різна.
/// </summary>
public sealed class CopyDialog : Window
{
    private readonly List<(string Group, CheckBox Box)> _boxes = [];

    public CopyDialog(IReadOnlyCollection<string> checkedGroups)
    {
        Title = "Копіювати правки";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (group, _) in StylePresets.SettingGroups)
        {
            var box = new CheckBox { Content = group, IsChecked = checkedGroups.Contains(group) };
            _boxes.Add((group, box));
            list.Children.Add(box);
        }

        var all = new Button { Content = "Усе", Padding = new Thickness(8, 2) };
        var none = new Button { Content = "Нічого", Padding = new Thickness(8, 2) };
        all.Click += (_, _) => _boxes.ForEach(b => b.Box.IsChecked = true);
        none.Click += (_, _) => _boxes.ForEach(b => b.Box.IsChecked = false);
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { all, none } };

        var ok = new Button { Content = "Копіювати", Classes = { "accent" }, IsDefault = true };
        var cancel = new Button { Content = "Скасувати", IsCancel = true };
        ok.Click += (_, _) =>
        {
            var chosen = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Group).ToHashSet();
            if (chosen.Count > 0) Close(chosen);
        };
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { cancel, ok },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "Що скопіювати з цього фото:", Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")) },
                list,
                quick,
                buttons,
            },
        };
    }
}
