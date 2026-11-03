using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Розділ «🩹 Відновлення»: відновлювальний пензель і клон. Мазок по плямі — і програма сама знаходить,
/// звідки взяти латку; джерело можна перетягнути. Латки накладаються до всіх правок, тож тон і колір їх «підхоплюють».
/// </summary>
public partial class MainWindow
{
    private const string HealSection = "🩹 Відновлення";

    private bool _healMode;
    private int _healIndex = -1;
    private HealMode _healTool = HealMode.Heal;
    private double _healSize = 0.015, _healFeather = 0.5, _healOpacity = 1;
    private bool _syncingHeal;

    private ToggleButton _healToggle = null!, _healButton = null!, _cloneButton = null!, _removeButton = null!;
    private Slider _healSizeSlider = null!, _healFeatherSlider = null!, _healOpacitySlider = null!;
    private TextBlock _healInfo = null!;
    private Button _healDelete = null!, _healClear = null!;

    private HealSpot? SelectedHeal => _healIndex >= 0 && _healIndex < _settings.Heals.Count ? _settings.Heals[_healIndex] : null;

    private void BuildHealSection()
    {
        var body = AddSection(HealSection);

        _healToggle = new ToggleButton
        {
            Content = "🩹 Прибирати плями (H)", Margin = new Thickness(0, 6, 0, 0), FontSize = 12, Classes = { "tool" },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_healToggle, "Мазок чи клік по плямі, пилинці, дроту — і їх не стане");
        _healToggle.IsCheckedChanged += (_, _) => SetHealMode(_healToggle.IsChecked == true);
        body.Children.Add(_healToggle);

        _healButton = new ToggleButton { Content = "Відновлення", FontSize = 12, Padding = new Thickness(10, 3), IsChecked = true, Classes = { "tool" } };
        _cloneButton = new ToggleButton { Content = "Клон", FontSize = 12, Padding = new Thickness(10, 3), Classes = { "tool" } };
        ToolTip.SetTip(_healButton, "Латка бере текстуру з джерела, а колір і світло — від місця, куди її кладуть");
        ToolTip.SetTip(_cloneButton, "Точна копія джерела, як є (для чітких країв і візерунків)");
        _removeButton = new ToggleButton { Content = "🪄 ШІ-видалення", FontSize = 12, Padding = new Thickness(10, 3), Classes = { "tool" } };
        ToolTip.SetTip(_removeButton, "Обведи пензлем предмет (людину, авто, смітник) — нейромережа домалює фон замість нього");
        _healButton.Click += (_, _) => SetHealTool(HealMode.Heal);
        _cloneButton.Click += (_, _) => SetHealTool(HealMode.Clone);
        _removeButton.Click += (_, _) => SetHealTool(HealMode.Remove);
        body.Children.Add(new WrapPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children = { Spaced(_healButton), Spaced(_cloneButton), Spaced(_removeButton) },
        });

        _healSizeSlider = PlainSlider(body, "Розмір ([ ])", 2, 100, _healSize * 1000, "0", v =>
        {
            _healSize = v / 1000;
            // Домальоване ШІ під інший розмір не перерахуєш без нового запуску мережі — розмір діє лише на нові мазки.
            if (!_syncingHeal && SelectedHeal is { Mode: not HealMode.Remove } h && h.Size != _healSize) ReplaceHeal(h with { Size = _healSize });
            UpdateHealOverlay();
        }, resetTo: 15);
        _healFeatherSlider = PlainSlider(body, "М'якість краю", 0, 100, _healFeather * 100, "0", v =>
        {
            _healFeather = v / 100;
            if (!_syncingHeal && SelectedHeal is { } h && h.Feather != _healFeather) ReplaceHeal(h with { Feather = _healFeather });
            UpdateHealOverlay();
        }, resetTo: 50);
        _healOpacitySlider = PlainSlider(body, "Непрозорість", 1, 100, _healOpacity * 100, "0", v =>
        {
            _healOpacity = v / 100;
            if (!_syncingHeal && SelectedHeal is { } h && h.Opacity != _healOpacity) ReplaceHeal(h with { Opacity = _healOpacity });
        }, resetTo: 100);

        _healInfo = new TextBlock { Classes = { "label" }, Margin = new Thickness(0, 8, 0, 4) };
        body.Children.Add(_healInfo);
        _healDelete = SmallButton("✕ Видалити вибрану", "Прибрати вибрану латку (Delete)", DeleteSelectedHeal);
        _healClear = SmallButton("Прибрати всі", "Прибрати всі латки з фото", () =>
        {
            CommitEdits();
            _settings.Heals = EquatableList<HealSpot>.Empty;
            _healIndex = -1;
            RefreshHeal();
            OnSettingsEdited();
        });
        body.Children.Add(new WrapPanel { Children = { Spaced(_healDelete), Spaced(_healClear) } });
        body.Children.Add(Hint("Програма сама знаходить, звідки взяти латку; пунктир показує джерело — його можна перетягнути. " +
                               "«ШІ-видалення» — обведи предмет повністю, з невеликим запасом. Клік по кружечку — вибрати латку. Esc — вийти."));

        HealView.StrokeFinished += points => _ = _healTool == HealMode.Remove ? AddRemovalAsync(points) : AddHealAsync(points);
        HealView.SpotSelected += index =>
        {
            _healIndex = index;
            RefreshHeal();
        };
        HealView.SourceMoved += (dx, dy) =>
        {
            if (SelectedHeal is { } h) ReplaceHeal(h with { SourceDX = dx, SourceDY = dy });
        };
        RefreshHeal();
    }

    private void SetHealMode(bool on)
    {
        if (on && _photoPath is null) on = false;
        if (on)
        {
            if (_maskIndex >= 0) SelectMask(-1);
            if (_cropMode) SetCropMode(false);
            if (_zoomed) SetZoom(false, null);
            SplitToggle.IsChecked = false;
            if (_sectionCollapse.TryGetValue(HealSection, out var setCollapsed)) setCollapsed(false);
        }
        else _healIndex = -1;
        _healMode = on;
        if (_healToggle.IsChecked != on) _healToggle.IsChecked = on;
        RefreshHeal();
    }

    private void SetHealTool(HealMode tool)
    {
        _healTool = tool;
        _healButton.IsChecked = tool == HealMode.Heal;
        _cloneButton.IsChecked = tool == HealMode.Clone;
        _removeButton.IsChecked = tool == HealMode.Remove;
        // Відновлення ↔ клон для вибраної латки — так; у ШІ-видалення й назад — ні (там зовсім інша латка).
        if (!_syncingHeal && tool != HealMode.Remove && SelectedHeal is { Mode: not HealMode.Remove } h && h.Mode != tool)
            ReplaceHeal(h with { Mode = tool });
    }

    private bool _removing;

    /// <summary>
    /// ШІ-видалення: мережа домальовує фон замість обведеного (у фоні, кілька секунд — на процесорі).
    /// Береться найдетальніший кадр, що вже є: повна роздільність, якщо довантажилась, інакше прев'ю.
    /// </summary>
    private async Task AddRemovalAsync(IReadOnlyList<MaskPoint> points)
    {
        if (_photoPath is not { } path || _preview is not { } preview) return;
        if (_removing)
        {
            ActionText.Text = "🪄 Зачекай — попереднє видалення ще рахується";
            return;
        }
        if (!AiModels.IsInstalled(AiModels.Inpaint) && await new ModelDownloadDialog(AiModels.Inpaint).ShowDialog<bool>(this) != true) return;
        if (_photoPath != path) return;

        CommitEdits();
        _removing = true;
        ActionText.Text = "🪄 ШІ прибирає предмет… (кілька секунд)";
        var spot = new HealSpot(HealMode.Remove, _healSize, _healFeather, _healOpacity, new(points), 0, 0);
        var settings = _settings.Copy();
        var source = _full ?? preview;
        try
        {
            void Progress(int done, int total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_removing) ActionText.Text = $"🪄 ШІ прибирає довгий предмет частинами: {done + 1} з {total}…";
            });
            var result = await Task.Run(() => Inpainter.Generate(ImageGeometry.Apply(source, settings, crop: false), spot, Progress));
            if (_photoPath != path) return;
            _settings.Heals = _settings.Heals.Add(result.Spot);
            _healIndex = _settings.Heals.Count - 1;
            RefreshHeal();
            OnSettingsEdited();
            ActionText.Text = $"🪄 Прибрано за {result.Milliseconds / 1000.0:0.0} с {(result.Gpu ? "на відеокарті" : "на процесорі")}. Не так — Ctrl+Z або Delete";
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidOperationException)
        {
            ShowWarning("Не вдалося прибрати.", ex);
        }
        finally
        {
            _removing = false;
        }
    }

    /// <summary>Нова латка: джерело шукається на полотні з уже накладеними латками, у фоні.</summary>
    private async Task AddHealAsync(IReadOnlyList<MaskPoint> points)
    {
        if (_photoPath is not { } path || _preview is not { } preview) return;
        CommitEdits();
        var spot = new HealSpot(_healTool, _healSize, _healFeather, _healOpacity, new(points), 0, 0);
        var settings = _settings.Copy();
        var (dx, dy) = await Task.Run(() => Healing.FindSource(ImageGeometry.Apply(preview, settings, crop: false), spot));
        if (_photoPath != path) return;
        _settings.Heals = _settings.Heals.Add(spot with { SourceDX = dx, SourceDY = dy });
        _healIndex = _settings.Heals.Count - 1;
        RefreshHeal();
        OnSettingsEdited();
    }

    private void ReplaceHeal(HealSpot spot)
    {
        if (_healIndex < 0 || _healIndex >= _settings.Heals.Count) return;
        _settings.Heals = _settings.Heals.SetItem(_healIndex, spot);
        UpdateHealOverlay();
        OnSettingsEdited();
    }

    private void DeleteSelectedHeal()
    {
        if (SelectedHeal is null) return;
        CommitEdits();
        _settings.Heals = _settings.Heals.RemoveAt(_healIndex);
        _healIndex = -1;
        RefreshHeal();
        OnSettingsEdited();
    }

    /// <summary>Кнопки, лічильник і повзунки — під поточний стан (повзунки показують вибрану латку).</summary>
    private void RefreshHeal()
    {
        if (_healInfo is null) return;
        if (_healIndex >= _settings.Heals.Count) _healIndex = -1;
        int count = _settings.Heals.Count;
        _healInfo.Text = count == 0 ? "Латок немає" : $"Латок: {count}" + (_healIndex >= 0 ? $" · вибрано {_healIndex + 1}-у" : "");
        _healDelete.IsEnabled = _healIndex >= 0;
        _healClear.IsEnabled = count > 0;
        if (SelectedHeal is { } h)
        {
            _syncingHeal = true;
            _healSizeSlider.Value = h.Size * 1000;
            _healFeatherSlider.Value = h.Feather * 100;
            _healOpacitySlider.Value = h.Opacity * 100;
            SetHealTool(h.Mode);
            _syncingHeal = false;
        }
        UpdateHealOverlay();
    }

    /// <summary>Латки на фото — лише в звичайному перегляді, як і рамки масок.</summary>
    private void UpdateHealOverlay()
    {
        if (_healInfo is null) return;
        bool show = _healMode && !_cropMode && !_zoomed && SplitToggle.IsChecked != true && _preview is not null;
        HealView.IsVisible = show;
        if (!show || _preview is null) return;
        var (w, h) = ImageGeometry.OutputSize(_preview.Width, _preview.Height, _settings, crop: true);
        HealView.Update(_settings.Heals, _healIndex, _settings, w, h, _healSize, _healFeather);
    }

    private void ChangeHealSize(int step) =>
        _healSizeSlider.Value = Math.Clamp(_healSizeSlider.Value + step, _healSizeSlider.Minimum, _healSizeSlider.Maximum);
}
