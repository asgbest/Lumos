using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Розділ «🎭 Стилі»: імпорт пресетів Lightroom і власних стилів, список за групами,
/// попередній перегляд під час наведення мишки й застосування кліком (одним кроком історії).
/// </summary>
public partial class MainWindow
{
    private const string NoGroup = "Без групи";

    private StackPanel _styleList = null!;
    private TextBlock _styleReport = null!;
    private Style? _hoverStyle;

    // Сила стилю: поки після застосування стилю нічого іншого не міняли, повзунок перераховує його від стану «до стилю».
    private double _styleAmount = 1;
    private Style? _lastStyle;
    private DevelopSettings? _beforeStyle, _afterStyle;
    private readonly HashSet<string> _openStyleGroups = [StyleLibrary.OwnGroup, NoGroup];

    private void BuildStylesSection()
    {
        var body = AddSection("🎭 Стилі");
        _sectionBodies.Remove(body); // імпортувати можна й без відкритого фото
        body.IsEnabled = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SmallButton("📥 Імпорт…", "Пресети Lightroom (.xmp, .lrtemplate, .zip) або стилі Lumos", () => _ = ImportStylesAsync()));
        buttons.Children.Add(SmallButton("💾 Зберегти поточні", "Зберегти поточні правки (без обрізки) як свій стиль", () => _ = SaveStyleAsync()));
        body.Children.Add(buttons);

        body.Children.Add(BuildStyleAmount());

        _styleReport = Hint("");
        _styleReport.IsVisible = false;
        body.Children.Add(_styleReport);

        _styleList = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(_styleList);
        RefreshStyleList();
    }

    /// <summary>Повзунок «Сила стилю», 0–200 %, як Amount у Lightroom. Подвійний клік по назві — 100 %.</summary>
    private Control BuildStyleAmount()
    {
        var value = new TextBlock { Classes = { "label" }, HorizontalAlignment = HorizontalAlignment.Right, Text = "100 %" };
        var label = new TextBlock { Text = "Сила стилю", Classes = { "label" }, Background = Avalonia.Media.Brushes.Transparent };
        var slider = new Slider { Minimum = 0, Maximum = 200, Value = 100, SmallChange = 1 };
        slider.ValueChanged += (_, e) =>
        {
            _styleAmount = Math.Round(e.NewValue) / 100;
            value.Text = $"{_styleAmount * 100:0} %";
            if (_hoverStyle is not null) RequestRender();
            // Щойно застосований стиль (і нічого іншого після нього) — перерахувати з новою силою.
            if (_lastStyle is { } style && _beforeStyle is { } before && _afterStyle == _settings && _photoPath is not null)
            {
                ApplySettings(ApplyStyleTo(before, style, _styleAmount));
                _afterStyle = _settings.Copy(); // уже з округленням повзунків
                OnSettingsEdited();
            }
        };
        label.DoubleTapped += (_, _) => slider.Value = 100;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        header.Children.Add(label);
        Grid.SetColumn(value, 1);
        header.Children.Add(value);
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(slider);
        ToolTip.SetTip(panel, "Наскільки сильно накладати стиль. Після кліку по стилю повзунок підлаштовує його вживу.");
        return panel;
    }

    private void ShowStyleReport(string text)
    {
        _styleReport.Text = text;
        _styleReport.IsVisible = text.Length > 0;
    }

    private void RefreshStyleList()
    {
        _styleList.Children.Clear();
        var entries = StyleLibrary.Load();
        if (entries.Count == 0)
        {
            _styleList.Children.Add(Hint("Тут з'являться стилі: імпортуй пресети Lightroom або збережи свої правки."));
            return;
        }

        var groups = entries
            .GroupBy(e => e.Style.Group.Length > 0 ? e.Style.Group : NoGroup)
            .OrderBy(g => g.Key == StyleLibrary.OwnGroup ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        foreach (var group in groups)
        {
            string key = group.Key;
            var items = new StackPanel { IsVisible = _openStyleGroups.Contains(key) };
            var chevron = new Avalonia.Controls.Shapes.Path
            {
                Data = Avalonia.Media.Geometry.Parse("M1,1 L6,6 L11,1"),
                Classes = { "chevron", "small" },
            };
            chevron.Classes.Set("collapsed", !items.IsVisible);
            var header = new DockPanel { Classes = { "styleGroup" } };
            DockPanel.SetDock(chevron, Dock.Left);
            header.Children.Add(chevron);
            header.Children.Add(new TextBlock { Text = $"{key}  ·  {group.Count()}", Classes = { "label" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            header.Tapped += (_, _) =>
            {
                items.IsVisible = !items.IsVisible;
                chevron.Classes.Set("collapsed", !items.IsVisible);
                if (items.IsVisible) _openStyleGroups.Add(key); else _openStyleGroups.Remove(key);
            };

            foreach (var entry in group.OrderBy(e => e.Style.Name, StringComparer.CurrentCultureIgnoreCase))
                items.Children.Add(StyleRow(entry));
            _styleList.Children.Add(header);
            _styleList.Children.Add(items);
        }
    }

    private Control StyleRow(StyleLibrary.Entry entry)
    {
        var style = entry.Style;
        bool empty = style.Settings.Count == 0;
        var button = new Button
        {
            Content = style.Name,
            Classes = { "styleItem" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Opacity = empty ? 0.45 : 1,
        };
        string skipped = string.Join(", ", style.Skipped);
        if (empty) ToolTip.SetTip(button, $"Lumos поки не вміє нічого з цього стилю: {skipped}");
        else if (style.Skipped.Count > 0) ToolTip.SetTip(button, $"Пропущено (Lumos поки не вміє): {skipped}");

        button.Click += (_, _) => ApplyStyle(style);
        // Наведення — показати, як буде; прибрали мишку — повернути як було.
        button.PointerEntered += (_, _) =>
        {
            if (_preview is null || empty) return;
            _hoverStyle = style;
            RequestRender();
        };
        button.PointerExited += (_, _) =>
        {
            if (_hoverStyle != style) return;
            _hoverStyle = null;
            RequestRender();
        };

        var delete = new MenuItem { Header = "🗑 Видалити з Lumos" };
        delete.Click += (_, _) =>
        {
            StyleLibrary.Delete(entry);
            RefreshStyleList();
        };
        button.ContextMenu = new ContextMenu { ItemsSource = new[] { delete } };
        return button;
    }

    private void ApplyStyle(Style style)
    {
        if (_photoPath is null)
        {
            ShowStyleReport("Спершу відкрий фото.");
            return;
        }
        if (style.Settings.Count == 0) return;

        _hoverStyle = null;
        double amount = _styleAmount;
        if (LibraryList.SelectedItems?.Count > 1)
        {
            var targets = SelectedOrCurrent();
            ApplyToPhotos(targets, (_, s) => StylePresets.Apply(s, style, amount));
            ActionText.Text = $"🎭 Стиль «{style.Name}» застосовано до {targets.Count} фото";
            return;
        }
        CommitEdits(); // попередні правки — окремий крок, щоб стиль можна було скасувати Ctrl+Z
        _lastStyle = style;
        _beforeStyle = _settings.Copy();
        ApplySettings(ApplyStyleTo(_settings, style, amount));
        _afterStyle = _settings.Copy(); // уже з округленням повзунків
        OnSettingsEdited();
        string where = SelectedMask is { } mask ? $" у масці «{mask.Name}»" : "";
        ActionText.Text = style.Skipped.Count > 0
            ? $"🎭 Стиль «{style.Name}»{where} (без: {string.Join(", ", style.Skipped)})"
            : $"🎭 Стиль «{style.Name}»{where}";
    }

    /// <summary>Стиль лягає на те, що зараз редагується: на все фото або — якщо вибрана маска — лише на її правки.</summary>
    private DevelopSettings ApplyStyleTo(DevelopSettings basis, Style style, double amount)
    {
        if (_maskIndex < 0 || _maskIndex >= basis.Masks.Count) return StylePresets.Apply(basis, style, amount);
        var result = basis.Copy();
        var mask = result.Masks[_maskIndex];
        result.Masks = result.Masks.SetItem(_maskIndex, mask with { Local = StylePresets.Apply(mask.Local, style, amount) });
        return result;
    }

    private async Task ImportStylesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Імпорт стилів",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Пресети Lightroom і стилі Lumos") { Patterns = [.. StylePresets.ImportExtensions.Select(e => "*." + e)] },
                FilePickerFileTypes.All,
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;

        ShowStyleReport("Імпортую…");
        var (imported, failed) = await Task.Run(() =>
        {
            var ok = new List<Style>();
            var bad = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var (styles, broken) = StylePresets.Import(path);
                    ok.AddRange(styles);
                    bad.AddRange(broken);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    bad.Add(Path.GetFileName(path));
                }
            }
            foreach (var style in ok) StyleLibrary.Add(style);
            return (ok, bad);
        });

        // Якщо імпортували одну-дві групи — одразу розгортаємо їх, щоб було видно, що з'явилось.
        var importedGroups = imported.Select(s => s.Group.Length > 0 ? s.Group : NoGroup).Distinct().ToList();
        if (importedGroups.Count <= 2) _openStyleGroups.UnionWith(importedGroups);
        RefreshStyleList();

        var report = new List<string> { $"Імпортовано стилів: {imported.Count}." };
        if (failed.Count > 0) report.Add($"Не вдалося прочитати: {string.Join(", ", failed.Take(5))}{(failed.Count > 5 ? "…" : "")}.");
        int empty = imported.Count(s => s.Settings.Count == 0);
        if (empty > 0) report.Add($"{empty} містять лише те, чого Lumos ще не вміє, — вони сірі.");
        var skipped = imported.SelectMany(s => s.Skipped).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        if (skipped.Count > 0) report.Add($"Поки пропускається: {string.Join(", ", skipped)}. Наведи на стиль — побачиш, що саме.");
        ShowStyleReport(string.Join(" ", report));
    }

    private async Task SaveStyleAsync()
    {
        if (_photoPath is null)
        {
            ShowStyleReport("Спершу відкрий фото й зроби правки.");
            return;
        }
        CommitEdits();
        string? name = await new NameDialog("Новий стиль", "Назва стилю (зберігаються всі правки, крім обрізки):", "Мій стиль")
            .ShowDialog<string?>(this);
        if (name is null) return;

        StyleLibrary.Add(StylePresets.FromSettings(name, _settings) with { Group = StyleLibrary.OwnGroup });
        _openStyleGroups.Add(StyleLibrary.OwnGroup);
        RefreshStyleList();
        ShowStyleReport($"💾 Стиль «{name}» збережено в «{StyleLibrary.OwnGroup}».");
    }
}
