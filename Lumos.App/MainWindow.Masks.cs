using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Розділ «🖌 Маски»: пензель, лінійна й радіальна маски. Коли маску вибрано, усі повзунки панелі
/// (світло, крива, колір, змішувач HSL, грейдування, ефекти, деталі) редагують її власні правки —
/// на відміну від Lightroom, де в масці лише урізаний набір.
/// </summary>
public partial class MainWindow
{
    private const string MasksSection = "🖌 Маски";

    private int _maskIndex = -1;
    private bool _showMaskOverlay;
    private double _brushSize = 0.06, _brushFeather = 0.5, _brushFlow = 1;
    private bool _brushErase;

    private StackPanel _maskList = null!;
    private StackPanel _maskOptions = null!, _brushOptions = null!, _radialOptions = null!;
    private CheckBox _invertBox = null!, _overlayBox = null!, _eraseBox = null!;
    private Slider _radialFeather = null!, _radialAngle = null!, _brushSizeSlider = null!;
    private bool _syncingMaskOptions; // повзунки маски оновлюються з коду — це не дія користувача

    /// <summary>Що зараз редагують повзунки: правки вибраної маски або всього фото.</summary>
    private DevelopSettings Target =>
        _maskIndex >= 0 && _maskIndex < _settings.Masks.Count ? _settings.Masks[_maskIndex].Local : _settings;

    private Mask? SelectedMask => _maskIndex >= 0 && _maskIndex < _settings.Masks.Count ? _settings.Masks[_maskIndex] : null;

    private void BuildMasksSection()
    {
        var body = AddSection(MasksSection);

        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        add.Children.Add(Spaced(SmallButton("+ 🖌 Пензель", "Малювати маску мишкою", () => AddMask(Mask.NewBrush(NextMaskName("Пензель"))))));
        add.Children.Add(Spaced(SmallButton("+ ▭ Лінійна", "Плавний перехід, напр. для неба", () => AddMask(Mask.NewLinear(NextMaskName("Лінійна"))))));
        add.Children.Add(Spaced(SmallButton("+ ◯ Радіальна", "Еліпс: виділити об'єкт чи зробити віньєтку", () => AddMask(Mask.NewRadial(NextMaskName("Радіальна"))))));
        add.Children.Add(Spaced(SmallButton("+ 🎨 Колір", "Усе в кадрі такого кольору — вибери піпеткою", () => AddRangeMask(MaskKind.ColorRange))));
        add.Children.Add(Spaced(SmallButton("+ ☀️ Яскравість", "Усе в кадрі в межах яскравості (напр. лише світлі ділянки)", () => AddRangeMask(MaskKind.LuminanceRange))));
        var landscape = new WrapPanel();
        foreach (var type in AiMaskTypes.All)
            (type.Landscape ? landscape : add).Children.Add(Spaced(SmallButton($"+ {type.Icon} {type.Name}",
                $"{type.Tip} — ШІ виділить сам (доправити — пензлем)", () => _ = AddAiMaskAsync(type))));
        body.Children.Add(add);
        body.Children.Add(new TextBlock { Text = "Ландшафт", Classes = { "label" }, Margin = new Thickness(0, 2, 0, 4) });
        body.Children.Add(landscape);

        _maskList = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(_maskList);

        _maskOptions = new StackPanel { IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        _invertBox = new CheckBox { Content = "Інвертувати (діє поза маскою)", FontSize = 12 };
        _invertBox.IsCheckedChanged += (_, _) =>
        {
            if (!_applyingSettings && SelectedMask is { } m && m.Invert != (_invertBox.IsChecked == true))
                ReplaceMask(m with { Invert = _invertBox.IsChecked == true });
        };
        _overlayBox = new CheckBox { Content = "Показати маску червоним (O)", FontSize = 12 };
        _overlayBox.IsCheckedChanged += (_, _) =>
        {
            _showMaskOverlay = _overlayBox.IsChecked == true;
            RequestRender();
        };
        _maskOptions.Children.Add(_invertBox);
        _maskOptions.Children.Add(_overlayBox);

        _brushOptions = new StackPanel();
        _brushSizeSlider = PlainSlider(_brushOptions, "Розмір пензля ([ ])", 1, 40, _brushSize * 100, "0", v => _brushSize = v / 100);
        PlainSlider(_brushOptions, "М'якість", 0, 100, _brushFeather * 100, "0", v => _brushFeather = v / 100);
        PlainSlider(_brushOptions, "Сила мазка", 1, 100, _brushFlow * 100, "0", v => _brushFlow = v / 100);
        _eraseBox = new CheckBox { Content = "Гумка (або тримай Alt)", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        _eraseBox.IsCheckedChanged += (_, _) =>
        {
            _brushErase = _eraseBox.IsChecked == true;
            UpdateMaskOverlay();
        };
        _brushOptions.Children.Add(_eraseBox);
        _maskOptions.Children.Add(_brushOptions);

        _radialOptions = new StackPanel();
        _radialFeather = PlainSlider(_radialOptions, "Розмиття краю", 0, 100, 50, "0", v =>
        {
            if (!_applyingSettings && !_syncingMaskOptions && SelectedMask is { } m && m.Feather != v) ReplaceMask(m with { Feather = v });
        }, resetTo: 50);
        _radialAngle = PlainSlider(_radialOptions, "Кут (або ручка над еліпсом)", -180, 180, 0, "+0°;−0°;0°", v =>
        {
            if (!_applyingSettings && !_syncingMaskOptions && SelectedMask is { } m && m.Angle != v) ReplaceMask(m with { Angle = v });
        }, resetTo: 0);
        _maskOptions.Children.Add(_radialOptions);
        BuildRangeOptions(_maskOptions);
        body.Children.Add(_maskOptions);

        body.Children.Add(Hint("Вибери маску — і всі повзунки нижче (світло, крива, колір, HSL, грейдування, ефекти) діятимуть лише в ній. Esc — назад до всього фото."));

        MaskView.MaskChanged += mask =>
        {
            if (SelectedMask is null) return;
            ReplaceMask(mask with { Local = SelectedMask.Local, Name = SelectedMask.Name, Visible = SelectedMask.Visible });
        };
        RefreshMaskList();
    }

    private static Control Spaced(Control c)
    {
        c.Margin = new Thickness(0, 0, 6, 6);
        return c;
    }

    /// <summary>Простий повзунок для налаштувань інструмента (не правок фото): назва, значення, дія при зміні.</summary>
    private static Slider PlainSlider(Panel parent, string label, double min, double max, double value, string format,
        Action<double> changed, double? resetTo = null)
    {
        var text = new TextBlock { Classes = { "label" }, HorizontalAlignment = HorizontalAlignment.Right, Text = value.ToString(format) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value };
        slider.ValueChanged += (_, e) =>
        {
            double v = Math.Round(e.NewValue);
            text.Text = v.ToString(format);
            changed(v);
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var name = new TextBlock { Text = label, Classes = { "label" }, Background = Brushes.Transparent };
        if (resetTo is { } reset) name.DoubleTapped += (_, _) => slider.Value = reset; // подвійний клік по назві — скинути
        header.Children.Add(name);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        parent.Children.Add(header);
        parent.Children.Add(slider);
        return slider;
    }

    private string NextMaskName(string kind)
    {
        for (int i = 1; ; i++)
        {
            string name = $"{kind} {i}";
            if (_settings.Masks.All(m => m.Name != name)) return name;
        }
    }

    private void AddMask(Mask mask)
    {
        if (_photoPath is null) return;
        CommitEdits();
        _settings.Masks = _settings.Masks.Add(mask);
        SelectMask(_settings.Masks.Count - 1);
        if (mask.Kind == MaskKind.Brush) ActionText.Text = "🖌 Малюй на фото, де має діяти маска";
        OnSettingsEdited();
    }

    /// <summary>Маска за кольором чи яскравістю на весь кадр — одразу з піпеткою й червоним підсвічуванням.</summary>
    private void AddRangeMask(MaskKind kind)
    {
        bool color = kind == MaskKind.ColorRange;
        var mask = color
            ? new Mask { Name = NextMaskName("Колір"), Kind = kind }
            : new Mask { Name = NextMaskName("Яскравість"), Kind = kind, RangeLumMin = 60 }; // для початку — світлі ділянки
        AddMask(mask);
        _overlayBox.IsChecked = true;
        SetRangePick(color ? RangePick.Color : RangePick.Light);
    }

    /// <summary>
    /// ШІ-маска: за потреби — завантаження моделі (з дозволу), потім нейромережа на кадрі (повернутому й вирівняному,
    /// але до обрізки — як і інші маски) і готова маска в списку. Доправити — пензлем по ній.
    /// </summary>
    private async Task AddAiMaskAsync(AiMaskType type)
    {
        if (_photoPath is not { } path || _preview is not { } preview) return;
        var model = type.Model;
        if (!AiModels.IsInstalled(model) && await new ModelDownloadDialog(model).ShowDialog<bool>(this) != true) return;
        if (_photoPath != path) return; // поки завантажувалось, відкрили інше фото

        string what = type.Find;
        ActionText.Text = $"🔎 Шукаю {what}…";
        var settings = _settings.Copy();
        var profile = _profile;
        try
        {
            var (raster, result) = await Task.Run(() => AiLooks.SegmentAiMask(type, path, preview, settings, profile));
            if (_photoPath != path) return;

            string name = NextMaskName(type.Name);
            AddMask(new Mask { Name = name, Kind = type.Kind, Raster = raster });
            string device = result.Gpu ? "на відеокарті" : "на процесорі";
            ActionText.Text = result.Peak < 0.3f
                ? $"{type.Icon} {type.Missing} в кадрі майже не знайдено ({result.Milliseconds} мс {device}) — домалюй пензлем, якщо треба"
                : $"✨ Маску «{name}» знайдено за {result.Milliseconds} мс {device}. Доправити — пензлем, Alt — стерти";
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidOperationException)
        {
            ShowWarning($"Не вдалося виділити {what}.", ex);
        }
    }

    /// <summary>Заміна маски (форма, інверсія, видимість) — як звичайна правка з Ctrl+Z.</summary>
    private void ReplaceMask(Mask mask)
    {
        if (_maskIndex < 0 || _maskIndex >= _settings.Masks.Count) return;
        _settings.Masks = _settings.Masks.SetItem(_maskIndex, mask);
        SyncMaskOptions();
        UpdateMaskOverlay();
        OnSettingsEdited();
    }

    private void SelectMask(int index)
    {
        _maskIndex = index < _settings.Masks.Count ? index : -1;
        if (_maskIndex >= 0 && _healMode) SetHealMode(false); // або маска, або латки — пензлі не змішуються
        if (_maskIndex < 0 && _showMaskOverlay) _overlayBox.IsChecked = false;
        SyncControls();
        RequestRender();
    }

    private void OnMaskDoneClick(object? sender, RoutedEventArgs e) => SelectMask(-1);

    /// <summary>Список масок, плашка «редагуєш маску», налаштування інструмента і рамка на фото — під поточний стан.</summary>
    private void RefreshMaskList()
    {
        if (_maskList is null) return;
        if (_maskIndex >= _settings.Masks.Count) _maskIndex = -1;
        _maskList.Children.Clear();
        for (int i = 0; i < _settings.Masks.Count; i++)
        {
            int index = i;
            var mask = _settings.Masks[i];
            string icon = AiMaskTypes.Of(mask.Kind)?.Icon ?? mask.Kind switch
            {
                MaskKind.Brush => "🖌", MaskKind.Linear => "▭", MaskKind.ColorRange => "🎨", MaskKind.LuminanceRange => "☀️", _ => "◯",
            };
            if (mask.HasRange && !mask.IsRange) icon += "🎯"; // уточнена за кольором/яскравістю
            var select = new ToggleButton
            {
                Content = $"{icon} {mask.Name}" + (mask.Invert ? " (інверсія)" : ""),
                IsChecked = i == _maskIndex,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 12,
                Padding = new Thickness(8, 3),
            };
            ToolTip.SetTip(select, "Клік — редагувати цю маску; подвійний клік — перейменувати");
            select.Click += (_, _) => SelectMask(index == _maskIndex ? -1 : index);
            select.DoubleTapped += async (_, _) =>
            {
                string? name = await new NameDialog("Назва маски", "Нова назва маски:", mask.Name).ShowDialog<string?>(this);
                if (name is null || index >= _settings.Masks.Count) return;
                _settings.Masks = _settings.Masks.SetItem(index, _settings.Masks[index] with { Name = name });
                OnSettingsEdited();
                RefreshMaskList();
            };
            var visible = SmallButton(mask.Visible ? "👁" : "◌", mask.Visible ? "Сховати маску" : "Показати маску", () =>
            {
                _settings.Masks = _settings.Masks.SetItem(index, _settings.Masks[index] with { Visible = !_settings.Masks[index].Visible });
                OnSettingsEdited();
                RefreshMaskList();
            });
            var delete = SmallButton("✕", "Видалити маску", () =>
            {
                CommitEdits();
                _settings.Masks = _settings.Masks.RemoveAt(index);
                if (_maskIndex == index) _maskIndex = -1;
                else if (_maskIndex > index) _maskIndex--;
                SyncControls();
                OnSettingsEdited();
            });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(visible, Dock.Right);
            delete.Margin = visible.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(delete);
            row.Children.Add(visible);
            row.Children.Add(select);
            _maskList.Children.Add(row);
        }

        var selected = SelectedMask;
        _maskOptions.IsVisible = selected is not null;
        _brushOptions.IsVisible = PaintsWithBrush(selected); // ШІ-маску й маску за кольором теж доправляють пензлем
        _radialOptions.IsVisible = selected?.Kind == MaskKind.Radial;
        SyncMaskOptions();
        MaskBanner.IsVisible = selected is not null;
        if (selected is not null)
            MaskBannerText.Text = $"🖌 Редагуєш маску «{selected.Name}»: повзунки нижче діють лише в ній.";
        UpdateMaskOverlay();
    }

    /// <summary>Рамка маски на фото — лише в звичайному перегляді (не в обрізці, 100 % чи «Поруч»).</summary>
    private void UpdateMaskOverlay()
    {
        var mask = SelectedMask;
        UpdateHealOverlay(); // ті самі умови показу (обрізка, 100 %, «Поруч»)
        UpdateFocusOverlay();
        UpdateGridOverlay();
        bool show = mask is not null && !_cropMode && !_zoomed && SplitToggle.IsChecked != true && _preview is not null;
        MaskView.IsVisible = show;
        if (!show || _preview is null) return;
        var (w, h) = ImageGeometry.OutputSize(_preview.Width, _preview.Height, _settings, crop: true);
        MaskView.Update(mask, _settings, w, h, _brushSize, _brushFeather, _brushFlow, _brushErase, _rangePick != RangePick.None);
    }

    private static bool PaintsWithBrush(Mask? mask) => mask is { Kind: MaskKind.Brush } or { IsAi: true } or { IsRange: true };

    /// <summary>Галочка інверсії й повзунки радіальної маски показують поточну маску (зокрема під час повороту ручкою).</summary>
    private void SyncMaskOptions()
    {
        var mask = SelectedMask;
        _syncingMaskOptions = true;
        SyncRangeOptions(mask);
        if (mask is not null)
        {
            _invertBox.IsChecked = mask.Invert;
            _radialFeather.Value = mask.Feather;
            _radialAngle.Value = mask.Angle;
        }
        _syncingMaskOptions = false;
    }

    private void ChangeBrushSize(int step)
    {
        if (!PaintsWithBrush(SelectedMask)) return;
        _brushSizeSlider.Value = Math.Clamp(_brushSizeSlider.Value + step, _brushSizeSlider.Minimum, _brushSizeSlider.Maximum);
        UpdateMaskOverlay();
    }
}
