using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Розділи «🔬 Об'єктив» (дисторсія, хроматичні аберації, віньєтування об'єктива) і «📐 Перспектива»
/// (Upright: авто/рівень/вертикаль/повна, повзунки перетворення, сітка). Рамка обрізки сама стискається, щоб не було чорних країв.
/// </summary>
public partial class MainWindow
{
    private const string LensSection = "🔬 Об'єктив", PerspectiveSection = "📐 Перспектива";

    private ToggleButton _gridToggle = null!;
    private CheckBox _lensProfileBox = null!;
    private TextBlock _lensProfileInfo = null!;
    private bool _opticsBusy;

    /// <summary>Галочка профілю — під поточне фото: недоступна, якщо камера профілю не записала.</summary>
    private void SyncLensProfile()
    {
        if (_lensProfileBox is null) return;
        var profile = _settings.LensProfileData;
        _lensProfileBox.IsEnabled = profile is not null;
        _lensProfileBox.IsChecked = profile is not null && _settings.LensProfile;
        _lensProfileInfo.Text = profile is not null
            ? $"Є у файлі: {profile.Describe()}. Повзунки нижче — поверх профілю."
            : "У цьому файлі профілю об'єктива немає (його пишуть камери Sony в ARW).";
    }

    private void BuildOpticsSections()
    {
        var lens = AddSection(LensSection);
        _lensProfileBox = new CheckBox { Content = "📷 Профіль об'єктива з RAW", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        ToolTip.SetTip(_lensProfileBox, "Поправки, які камера сама записала у файл (як у її власних JPEG і в Lightroom)");
        _lensProfileBox.IsCheckedChanged += (_, _) =>
        {
            bool on = _lensProfileBox.IsChecked == true;
            if (_applyingSettings || _settings.LensProfile == on) return;
            CommitEdits();
            _settings.LensProfile = on;
            AfterWarpChanged();
            ActionText.Text = on ? "📷 Профіль об'єктива увімкнено" : "📷 Профіль об'єктива вимкнено";
        };
        lens.Children.Add(_lensProfileBox);
        _lensProfileInfo = Hint("");
        lens.Children.Add(_lensProfileInfo);
        AddWarpSlider(lens, new("Дисторсія", -100, 100, Signed, s => s.LensDistortion, (s, v) => s.LensDistortion = v, GlobalOnly: true));
        lens.Children.Add(Hint("+ прибирає «бочку» (краї випнуті назовні, як у ширококутних), − — «подушку»."));
        lens.Children.Add(SubHeader("Хроматичні аберації"));
        var caButton = SmallButton("🪄 Прибрати кольорову облямівку",
            "Сам знаходить, наскільки червоний і синій канали зсунуті відносно зеленого на краях кадру", () => _ = AutoChromaticAsync());
        caButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        caButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        caButton.Margin = new Thickness(0, 6, 0, 0);
        lens.Children.Add(caButton);
        AddWarpSlider(lens, new("Червоний / блакитний", -100, 100, Signed, s => s.LensCaRed, (s, v) => s.LensCaRed = v, GlobalOnly: true));
        AddWarpSlider(lens, new("Синій / жовтий", -100, 100, Signed, s => s.LensCaBlue, (s, v) => s.LensCaBlue = v, GlobalOnly: true));
        lens.Children.Add(SubHeader("Віньєтування об'єктива"));
        AddSlider(lens, new("Сила", -100, 100, Signed, s => s.LensVignette, (s, v) => s.LensVignette = v, GlobalOnly: true));
        AddSlider(lens, new("Середина", 0, 100, "0", s => s.LensVignetteMid, (s, v) => s.LensVignetteMid = v, Default: 50, GlobalOnly: true));
        lens.Children.Add(Hint("+ висвітлює темні кути від об'єктива (до обрізки; для художньої віньєтки — «Ефекти»)."));

        var persp = AddSection(PerspectiveSection);
        persp.Children.Add(SubHeader("Авто (Upright)"));
        var upright = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, mode, tip) in new (string, UprightMode?, string)[]
                 {
                     ("Авто", UprightMode.Auto, "Збалансовано: вертикалі, горизонт і помірна перспектива без надмірних викривлень"),
                     ("Рівень", UprightMode.Level, "Лише поворот: горизонт і вертикалі рівно"),
                     ("Вертикаль", UprightMode.Vertical, "Поворот і вертикальна перспектива: стіни будинків паралельні"),
                     ("Повна", UprightMode.Full, "Вертикальна й горизонтальна перспектива та поворот (фасад «в анфас»)"),
                     ("Вимк.", null, "Прибрати всю перспективу"),
                 })
        {
            var button = SmallButton(label, tip, () =>
            {
                if (mode is { } m) _ = AutoUprightAsync(m); else ResetPerspective();
            });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Margin = new Thickness(0, 0, 4, 4);
            upright.Children.Add(button);
        }
        persp.Children.Add(upright);

        _gridToggle = new ToggleButton
        {
            Content = "▦ Сітка на фото", FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Classes = { "tool" },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_gridToggle, "Сітка, щоб на око звіряти вертикалі й горизонт");
        _gridToggle.IsCheckedChanged += (_, _) => UpdateGridOverlay();
        persp.Children.Add(_gridToggle);

        persp.Children.Add(SubHeader("Вручну"));
        AddWarpSlider(persp, new("Вертикаль", -100, 100, Signed, s => s.PerspVertical, (s, v) => s.PerspVertical = v, GlobalOnly: true));
        AddWarpSlider(persp, new("Горизонталь", -100, 100, Signed, s => s.PerspHorizontal, (s, v) => s.PerspHorizontal = v, GlobalOnly: true));
        AddWarpSlider(persp, new("Поворот", -10, 10, "+0.0°;−0.0°;0.0°", s => s.PerspRotate, (s, v) => s.PerspRotate = v, Decimals: 1, GlobalOnly: true));
        AddWarpSlider(persp, new("Пропорції", -100, 100, Signed, s => s.PerspAspect, (s, v) => s.PerspAspect = v, GlobalOnly: true));
        AddWarpSlider(persp, new("Масштаб", 50, 150, "0'%'", s => s.PerspScale, (s, v) => s.PerspScale = v, Default: 100, GlobalOnly: true));
        AddWarpSlider(persp, new("Зсув по X", -100, 100, Signed, s => s.PerspX, (s, v) => s.PerspX = v, GlobalOnly: true));
        AddWarpSlider(persp, new("Зсув по Y", -100, 100, Signed, s => s.PerspY, (s, v) => s.PerspY = v, GlobalOnly: true));
        persp.Children.Add(Hint("«Вертикаль» − розширює верх (будинок, знятий знизу, перестає «падати назад»). " +
                                "Рамка обрізки сама зменшується, щоб у кадр не потрапили чорні краї."));
    }

    /// <summary>Повзунок, що рухає пікселі: після зміни рамка обрізки підлаштовується, як і для «Вирівнювання».</summary>
    private void AddWarpSlider(Panel body, Adjustment def) =>
        AddSlider(body, def with
        {
            Set = (s, v) =>
            {
                def.Set(s, v);
                if (_applyingSettings) return;
                var (w, h) = CanvasSize();
                ImageGeometry.FitCrop(s, w, h, _cropAspect);
                UpdateCropOverlay();
            },
        });

    private void ResetPerspective()
    {
        CommitEdits();
        (_settings.PerspVertical, _settings.PerspHorizontal, _settings.PerspRotate) = (0, 0, 0);
        (_settings.PerspAspect, _settings.PerspScale, _settings.PerspX, _settings.PerspY) = (0, 100, 0, 0);
        AfterWarpChanged();
        ActionText.Text = "📐 Перспективу прибрано";
    }

    private void AfterWarpChanged()
    {
        var (w, h) = CanvasSize();
        ImageGeometry.FitCrop(_settings, w, h, _cropAspect);
        UpdateCropOverlay();
        SyncControls();
        OnSettingsEdited();
    }

    private async Task AutoUprightAsync(UprightMode mode)
    {
        if (_preview is not { } preview || _opticsBusy) return;
        _opticsBusy = true;
        string? path = _photoPath;
        try
        {
            ActionText.Text = "📐 Шукаю лінії…";
            var settings = _settings.Copy();
            var result = await Task.Run(() => Upright.Solve(Upright.Canvas(preview, settings), mode));
            if (_photoPath != path) return;
            if (result is null)
            {
                ActionText.Text = "📐 На кадрі замало прямих ліній — підправ «Вручну»";
                return;
            }
            CommitEdits();
            (_settings.PerspVertical, _settings.PerspHorizontal, _settings.PerspRotate) = (result.Vertical, result.Horizontal, result.Rotate);
            _settings.Angle = 0; // поворот уже в перспективі
            AfterWarpChanged();
            ActionText.Text = $"📐 Вирівняно за {result.Verticals} вертикальними й {result.Horizontals} горизонтальними лініями";
        }
        finally
        {
            _opticsBusy = false;
        }
    }

    private async Task AutoChromaticAsync()
    {
        if (_preview is null || _opticsBusy) return;
        _opticsBusy = true;
        string? path = _photoPath;
        try
        {
            ActionText.Text = "🪄 Шукаю кольорову облямівку на повному кадрі…";
            // Зсув каналів — частки пікселя, тож потрібен повний кадр; поворот і віддзеркалення на радіальний масштаб не впливають.
            var full = _full ?? (_fullTask is { } task ? await task : _preview);
            if (_photoPath != path) return;
            // Якщо діє профіль об'єктива — шукаємо лише те, що лишилось після нього.
            var profile = _settings.LensProfile ? _settings.LensProfileData : null;
            var (red, blue) = await Task.Run(() => ChromaticAberration.Estimate(profile is null ? full
                : ImageGeometry.Apply(full, new DevelopSettings { LensProfile = true, LensProfileData = profile }, crop: false)));
            if (_photoPath != path) return;
            CommitEdits();
            (_settings.LensCaRed, _settings.LensCaBlue) = (red, blue);
            AfterWarpChanged();
            ActionText.Text = red == 0 && blue == 0
                ? "🪄 Кольорової облямівки не знайшов"
                : $"🪄 Облямівку прибрано: червоний {red:+0;−0;0}, синій {blue:+0;−0;0}";
        }
        finally
        {
            _opticsBusy = false;
        }
    }

    private void UpdateGridOverlay()
    {
        if (_gridToggle is null) return;
        bool show = _gridToggle.IsChecked == true && !_cropMode && !_zoomed && SplitToggle.IsChecked != true && _preview is not null;
        GridView.IsVisible = show;
        if (!show || _preview is null) return;
        var (w, h) = ImageGeometry.OutputSize(_preview.Width, _preview.Height, _settings, crop: true);
        GridView.Update(w, h);
    }
}
