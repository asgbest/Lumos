using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lumos.Core;
using System.Globalization;

namespace Lumos.App;

/// <summary>Права панель: секції з повзунками, крива, змішувач кольорів, колеса грейдування, обрізка.</summary>
public partial class MainWindow
{
    private sealed record Adjustment(
        string Label, double Min, double Max, string Format,
        Func<DevelopSettings, double> Get, Action<DevelopSettings, double> Set,
        double Default = 0, int Decimals = 0, bool GlobalOnly = false);

    private const string Signed = "+0;−0;0";

    private static readonly (HslBand Band, string Name, string Color)[] HslBands =
    [
        (HslBand.Red, "Червоний", "#E53935"),
        (HslBand.Orange, "Помаранчевий", "#FB8C00"),
        (HslBand.Yellow, "Жовтий", "#FDD835"),
        (HslBand.Green, "Зелений", "#43A047"),
        (HslBand.Aqua, "Бірюзовий", "#00ACC1"),
        (HslBand.Blue, "Синій", "#1E88E5"),
        (HslBand.Purple, "Фіолетовий", "#8E24AA"),
        (HslBand.Magenta, "Пурпуровий", "#D81B60"),
    ];

    private static readonly (string Label, double? Ratio, bool Original)[] AspectChoices =
    [
        ("Вільні пропорції", null, false),
        ("Як в оригіналу", null, true),
        ("Instagram Stories · 9 : 16", 9 / 16.0, false),
        ("1 : 1", 1, false),
        ("4 : 5", 4 / 5.0, false),
        ("5 : 4", 5 / 4.0, false),
        ("2 : 3", 2 / 3.0, false),
        ("3 : 2", 3 / 2.0, false),
        ("16 : 9", 16 / 9.0, false),
    ];

    // Колеса грейдування: назва, як прочитати й записати значення.
    private static readonly (string Name, Func<DevelopSettings, GradeWheel> Get, Action<DevelopSettings, GradeWheel> Set)[] Wheels =
    [
        ("Тіні", s => s.GradeShadows, (s, w) => s.GradeShadows = w),
        ("Середні тони", s => s.GradeMidtones, (s, w) => s.GradeMidtones = w),
        ("Світлі", s => s.GradeHighlights, (s, w) => s.GradeHighlights = w),
        ("Загальне", s => s.GradeGlobal, (s, w) => s.GradeGlobal = w),
    ];

    private readonly List<(Adjustment Def, Slider Slider, Action ShowValue)> _sliders = [];
    private readonly List<Control> _sectionBodies = [];
    private readonly Dictionary<string, Action<bool>> _sectionCollapse = [];

    private CurveEditor _curveEditor = null!;
    private readonly List<ToggleButton> _curveChannelButtons = [];
    private int _curveChannel; // 0 — RGB, 1 — R, 2 — G, 3 — B

    private HslBand _hslBand = HslBand.Red;
    private readonly List<Border> _hslDots = [];
    private TextBlock _hslBandName = null!;

    private readonly ColorWheel[] _wheels = new ColorWheel[4];
    private readonly TextBlock[] _wheelValues = new TextBlock[4];

    private ComboBox _aspectBox = null!;
    private double? _cropAspect;
    private bool _aspectIsOriginal;

    private const string CropSection = "✂️ Обрізка";

    private void BuildPanels()
    {
        BuildStylesSection();
        BuildCropSection();
        BuildMasksSection();
        BuildHealSection();

        var light = AddSection("☀️ Світло");
        AddSlider(light, new("Експозиція", -5, 5, "+0.00;−0.00;0.00", s => s.Exposure, (s, v) => s.Exposure = v, Decimals: 2));
        AddSlider(light, new("Контраст", -100, 100, Signed, s => s.Contrast, (s, v) => s.Contrast = v));
        AddSlider(light, new("Світлі ділянки", -100, 100, Signed, s => s.Highlights, (s, v) => s.Highlights = v));
        AddSlider(light, new("Тіні", -100, 100, Signed, s => s.Shadows, (s, v) => s.Shadows = v));
        AddSlider(light, new("Білі", -100, 100, Signed, s => s.Whites, (s, v) => s.Whites = v));
        AddSlider(light, new("Чорні", -100, 100, Signed, s => s.Blacks, (s, v) => s.Blacks = v));

        BuildCurveSection();

        var color = AddSection("🎨 Колір");
        AddSlider(color, new("Температура", -100, 100, Signed, s => s.Temperature, (s, v) => s.Temperature = v));
        AddSlider(color, new("Відтінок", -100, 100, Signed, s => s.Tint, (s, v) => s.Tint = v));
        AddSlider(color, new("Яскравість кольорів", -100, 100, Signed, s => s.Vibrance, (s, v) => s.Vibrance = v));
        AddSlider(color, new("Насиченість", -100, 100, Signed, s => s.Saturation, (s, v) => s.Saturation = v));

        BuildHslSection();
        BuildGradingSection();

        var effects = AddSection("✨ Ефекти");
        AddSlider(effects, new("Текстура", -100, 100, Signed, s => s.Texture, (s, v) => s.Texture = v));
        AddSlider(effects, new("Чіткість", -100, 100, Signed, s => s.Clarity, (s, v) => s.Clarity = v));
        AddSlider(effects, new("Видалення димки", -100, 100, Signed, s => s.Dehaze, (s, v) => s.Dehaze = v));
        effects.Children.Add(SubHeader("Віньєтка"));
        AddSlider(effects, new("Сила", -100, 100, Signed, s => s.VignetteAmount, (s, v) => s.VignetteAmount = v));
        AddSlider(effects, new("Середина", 0, 100, "0", s => s.VignetteMidpoint, (s, v) => s.VignetteMidpoint = v, Default: 50));
        AddSlider(effects, new("Розмиття краю", 0, 100, "0", s => s.VignetteFeather, (s, v) => s.VignetteFeather = v, Default: 50));
        AddSlider(effects, new("Округлість", -100, 100, Signed, s => s.VignetteRoundness, (s, v) => s.VignetteRoundness = v));
        effects.Children.Add(SubHeader("Зерно"));
        AddSlider(effects, new("Сила", 0, 100, "0", s => s.GrainAmount, (s, v) => s.GrainAmount = v));
        AddSlider(effects, new("Розмір", 0, 100, "0", s => s.GrainSize, (s, v) => s.GrainSize = v, Default: 25));
        AddSlider(effects, new("Шорсткість", 0, 100, "0", s => s.GrainRoughness, (s, v) => s.GrainRoughness = v, Default: 50));

        BuildLensBlurSection();

        var detail = AddSection("🔍 Деталі");
        detail.Children.Add(SubHeader("Різкість"));
        AddSlider(detail, new("Сила", 0, 150, "0", s => s.SharpenAmount, (s, v) => s.SharpenAmount = v));
        AddSlider(detail, new("Радіус", 0.5, 3, "0.0", s => s.SharpenRadius, (s, v) => s.SharpenRadius = v, Default: 1, Decimals: 1));
        AddSlider(detail, new("Деталізація", 0, 100, "0", s => s.SharpenDetail, (s, v) => s.SharpenDetail = v, Default: 25));
        AddSlider(detail, new("Маскування", 0, 100, "0", s => s.SharpenMasking, (s, v) => s.SharpenMasking = v));
        detail.Children.Add(SubHeader("Шумозаглушення"));
        AddSlider(detail, new("Яскравість", 0, 100, "0", s => s.NoiseReduction, (s, v) => s.NoiseReduction = v));
        AddSlider(detail, new("Колір", 0, 100, "0", s => s.ColorNoiseReduction, (s, v) => s.ColorNoiseReduction = v));
        detail.Children.Add(Hint("Різкість і шум точно видно в масштабі 100 %: подвійний клік по фото або клавіша Z."));

        BuildOpticsSections();
    }

    private static TextBlock SubHeader(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse("#C8C8C8")),
        Margin = new Thickness(0, 12, 0, 0),
    };

    /// <summary>Секція, що згортається кліком по заголовку; стан запам'ятовується між запусками.</summary>
    private StackPanel AddSection(string title)
    {
        var body = new StackPanel { IsEnabled = false };
        var chevron = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M1,1 L6,6 L11,1"), Classes = { "chevron" } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "sectionHeader" } };
        header.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
        Grid.SetColumn(chevron, 1);
        header.Children.Add(chevron);

        void SetCollapsed(bool collapsed)
        {
            body.IsVisible = !collapsed;
            chevron.Classes.Set("collapsed", collapsed);
            if (collapsed) _ui.CollapsedSections.Add(title); else _ui.CollapsedSections.Remove(title);
        }
        body.IsVisible = !_ui.CollapsedSections.Contains(title);
        chevron.Classes.Set("collapsed", !body.IsVisible);
        header.Tapped += (_, _) =>
        {
            SetCollapsed(body.IsVisible);
            _ui.Save();
        };
        _sectionCollapse[title] = SetCollapsed;

        AdjustmentsPanel.Children.Add(header);
        AdjustmentsPanel.Children.Add(body);
        _sectionBodies.Add(body);
        return body;
    }

    private Slider AddSlider(Panel body, Adjustment def)
    {
        var value = new TextBox { Classes = { "value" }, HorizontalAlignment = HorizontalAlignment.Right };
        var label = new TextBlock { Text = def.Label, Classes = { "label" }, Background = Brushes.Transparent };
        var slider = new Slider
        {
            Minimum = def.Min,
            Maximum = def.Max,
            Value = def.Default,
            SmallChange = (def.Max - def.Min) / 200,
        };

        void ShowValue() => value.Text = slider.Value.ToString(def.Format);
        ShowValue();
        slider.ValueChanged += (_, e) =>
        {
            def.Set(def.GlobalOnly ? _settings : Target, Math.Round(e.NewValue, def.Decimals));
            ShowValue();
            OnSettingsEdited();
        };
        // Скидання — до стартового значення цього фото (для RAW різкість 40, а не 0).
        // У масці «скинути» — це нуль (як усе фото); для фото — стартове значення (для RAW різкість 40).
        label.DoubleTapped += (_, _) => slider.Value =
            _maskIndex >= 0 && !def.GlobalOnly ? def.Get(new DevelopSettings())
            : _photoPath is { } path ? def.Get(DevelopSettings.DefaultFor(path)) : def.Default;

        // Значення можна вписати вручну: Enter — застосувати, Esc — скасувати.
        // Приймає і кому, і крапку, і «−»; зайве обрізається до меж повзунка.
        void CommitValue()
        {
            string text = (value.Text ?? "").Trim().TrimEnd('°').Replace('−', '-').Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed))
                slider.Value = Math.Clamp(typed, def.Min, def.Max);
            ShowValue();
        }
        value.GotFocus += (_, _) => Dispatcher.UIThread.Post(value.SelectAll);
        value.LostFocus += (_, _) => CommitValue();
        value.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Escape) ShowValue();
            Focus(); // фокус — на вікно: LostFocus застосує значення, а Ctrl+Z тощо далі працюють
            e.Handled = true;
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        header.Children.Add(label);
        Grid.SetColumn(value, 1);
        header.Children.Add(value);

        body.Children.Add(header);
        body.Children.Add(slider);
        _sliders.Add((def, slider, ShowValue));
        return slider;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.Parse("#808080")),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private static Button SmallButton(string text, string tip, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 3), FontSize = 12 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    // ── Обрізка ────────────────────────────────────────────────────────────────

    private void BuildCropSection()
    {
        var body = AddSection(CropSection);

        _aspectBox = new ComboBox
        {
            ItemsSource = AspectChoices.Select(a => a.Label).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _aspectBox.SelectionChanged += (_, _) => OnAspectChosen();
        body.Children.Add(_aspectBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(SmallButton("↺ 90°", "Повернути проти годинникової стрілки", () => RotateCanvas(-90)));
        buttons.Children.Add(SmallButton("↻ 90°", "Повернути за годинниковою стрілкою", () => RotateCanvas(90)));
        buttons.Children.Add(SmallButton("⇆", "Віддзеркалити зліва направо", FlipCanvas));
        buttons.Children.Add(SmallButton("Скинути", "Прибрати обрізку, поворот і вирівнювання", ResetGeometry));
        body.Children.Add(buttons);

        AddSlider(body, new("Вирівнювання", -45, 45, "+0.0°;−0.0°;0.0°", s => s.Angle, (s, v) =>
        {
            s.Angle = v;
            if (_applyingSettings) return;
            // Рамка сама зменшується, щоб у кадр не потрапили чорні кути повернутого фото.
            var (w, h) = CanvasSize();
            ImageGeometry.FitCrop(s, w, h, _cropAspect);
            UpdateCropOverlay();
        }, Decimals: 1, GlobalOnly: true));

        body.Children.Add(Hint("Рамку тягни прямо на фото в режимі «✂️ Обрізка» (клавіша R, Enter — готово)."));
    }

    private (int Width, int Height) CanvasSize() =>
        _preview is null ? (1, 1) : ImageGeometry.OrientedSize(_preview.Width, _preview.Height, _settings.Rotation);

    private void UpdateCropOverlay()
    {
        var (w, h) = CanvasSize();
        CropView.Aspect = _cropAspect;
        CropView.Update(w, h, WarpParams.Of(_settings), new Rect(_settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH));
    }

    private void OnCropDragged(Rect crop)
    {
        (_settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH) = (crop.X, crop.Y, crop.Width, crop.Height);
        OnSettingsEdited();
    }

    private void OnAspectChosen()
    {
        if (_applyingSettings || _aspectBox.SelectedIndex < 0) return;
        var choice = AspectChoices[_aspectBox.SelectedIndex];
        var (w, h) = CanvasSize();
        _aspectIsOriginal = choice.Original;
        _cropAspect = choice.Original ? w / (double)h : choice.Ratio;
        if (_cropAspect is { } aspect)
        {
            ImageGeometry.FitCrop(_settings, w, h, aspect);
            OnSettingsEdited();
        }
        UpdateCropOverlay();
    }

    private void RotateCanvas(int degrees)
    {
        _settings.Rotation = ((_settings.Rotation + degrees) % 360 + 360) % 360;
        (_settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH) = (0, 0, 1, 1);
        var (w, h) = CanvasSize();
        if (_aspectIsOriginal) _cropAspect = w / (double)h;
        if (_cropAspect is not null || WarpParams.Of(_settings).MovesPixels) ImageGeometry.FitCrop(_settings, w, h, _cropAspect);
        UpdateCropOverlay();
        OnSettingsEdited();
    }

    private void FlipCanvas()
    {
        // Дзеркальне фото, вирівняне на кут θ, — те саме, що дзеркальне оригінальне, вирівняне на −θ.
        _settings.FlipH = !_settings.FlipH;
        _settings.Angle = -_settings.Angle;
        (_settings.PerspHorizontal, _settings.PerspRotate, _settings.PerspX) = (-_settings.PerspHorizontal, -_settings.PerspRotate, -_settings.PerspX);
        _settings.CropX = 1 - _settings.CropX - _settings.CropW;
        SyncControls();
        OnSettingsEdited();
    }

    private void ResetGeometry()
    {
        _settings.Rotation = 0;
        _settings.FlipH = false;
        _settings.Angle = 0;
        (_settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH) = (0, 0, 1, 1);
        SetAspectChoice(0);
        SyncControls();
        OnSettingsEdited();
    }

    private void SetAspectChoice(int index)
    {
        bool applying = _applyingSettings;
        _applyingSettings = true;
        _aspectBox.SelectedIndex = index;
        _applyingSettings = applying;
        _cropAspect = null;
        _aspectIsOriginal = false;
    }

    // ── Тонова крива ───────────────────────────────────────────────────────────

    private static readonly (string Label, Color Color)[] CurveChannels =
    [
        ("RGB", Colors.White),
        ("R", Color.Parse("#FF6B6B")),
        ("G", Color.Parse("#6BDB7A")),
        ("B", Color.Parse("#6B9BFF")),
    ];

    private static Curve GetCurve(DevelopSettings s, int channel) => channel switch
    {
        1 => s.CurveRed,
        2 => s.CurveGreen,
        3 => s.CurveBlue,
        _ => s.CurveMaster,
    };

    private static void SetCurve(DevelopSettings s, int channel, Curve curve)
    {
        switch (channel)
        {
            case 1: s.CurveRed = curve; break;
            case 2: s.CurveGreen = curve; break;
            case 3: s.CurveBlue = curve; break;
            default: s.CurveMaster = curve; break;
        }
    }

    private void BuildCurveSection()
    {
        var body = AddSection("📈 Тонова крива");

        var channels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 6) };
        for (int i = 0; i < CurveChannels.Length; i++)
        {
            int channel = i;
            var button = new ToggleButton
            {
                Content = CurveChannels[i].Label,
                Foreground = new SolidColorBrush(CurveChannels[i].Color),
                Padding = new Thickness(10, 2),
                FontSize = 12,
                IsChecked = i == 0,
            };
            button.Click += (_, _) => SelectCurveChannel(channel);
            _curveChannelButtons.Add(button);
            channels.Children.Add(button);
        }
        var reset = SmallButton("↺", "Скинути криву цього каналу", () =>
        {
            SetCurve(Target, _curveChannel, Curve.Identity);
            _curveEditor.SetCurve(Curve.Identity);
            OnSettingsEdited();
        });
        var row = new DockPanel();
        DockPanel.SetDock(reset, Dock.Right);
        row.Children.Add(reset);
        row.Children.Add(channels);
        body.Children.Add(row);

        _curveEditor = new CurveEditor { Height = 230 };
        _curveEditor.CurveChanged += curve =>
        {
            SetCurve(Target, _curveChannel, curve);
            OnSettingsEdited();
        };
        body.Children.Add(_curveEditor);
        body.Children.Add(Hint("Клік — нова точка, тягни — змінити. Подвійний клік або витягнути за край — видалити."));
    }

    private void SelectCurveChannel(int channel)
    {
        _curveChannel = channel;
        for (int i = 0; i < _curveChannelButtons.Count; i++) _curveChannelButtons[i].IsChecked = i == channel;
        _curveEditor.CurveColor = CurveChannels[channel].Color;
        _curveEditor.SetCurve(GetCurve(Target, channel));
        UpdateCurveHistogram();
    }

    /// <summary>Гістограма під кривою: свого каналу або (для RGB) середня по трьох.</summary>
    private void UpdateCurveHistogram()
    {
        const int bins = Developer.HistogramBins;
        var data = new int[bins];
        for (int i = 0; i < bins; i++)
            data[i] = _curveChannel == 0
                ? (_histogram[i] + _histogram[bins + i] + _histogram[2 * bins + i]) / 3
                : _histogram[(_curveChannel - 1) * bins + i];
        _curveEditor.SetHistogram(data);
    }

    // ── Змішувач кольорів ──────────────────────────────────────────────────────

    private void BuildHslSection()
    {
        var body = AddSection("🌈 Змішувач кольорів");

        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (band, name, color) in HslBands)
        {
            var dot = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.Parse(color)),
                BorderThickness = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(dot, name);
            dot.PointerPressed += (_, _) => SelectHslBand(band);
            _hslDots.Add(dot);
            dots.Children.Add(dot);
        }
        body.Children.Add(dots);
        _hslBandName = new TextBlock { Classes = { "label" }, Margin = new Thickness(0, 2, 0, 0) };
        body.Children.Add(_hslBandName);

        AddSlider(body, new("Відтінок", -100, 100, Signed, s => s.GetHsl(_hslBand).Hue,
            (s, v) => s.SetHsl(_hslBand, s.GetHsl(_hslBand) with { Hue = v })));
        AddSlider(body, new("Насиченість", -100, 100, Signed, s => s.GetHsl(_hslBand).Saturation,
            (s, v) => s.SetHsl(_hslBand, s.GetHsl(_hslBand) with { Saturation = v })));
        AddSlider(body, new("Яскравість", -100, 100, Signed, s => s.GetHsl(_hslBand).Luminance,
            (s, v) => s.SetHsl(_hslBand, s.GetHsl(_hslBand) with { Luminance = v })));

        SelectHslBand(HslBand.Red);
    }

    private void SelectHslBand(HslBand band)
    {
        _hslBand = band;
        for (int i = 0; i < _hslDots.Count; i++)
            _hslDots[i].BorderBrush = HslBands[i].Band == band ? Brushes.White : Brushes.Transparent;
        _hslBandName.Text = HslBands.First(b => b.Band == band).Name;
        SyncControls(); // повзунки показують значення вибраного кольору
    }

    // ── Кольорове грейдування ──────────────────────────────────────────────────

    private void BuildGradingSection()
    {
        var body = AddSection("🎡 Кольорове грейдування");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        for (int i = 0; i < Wheels.Length; i++)
        {
            int index = i;
            var cell = new StackPanel { Margin = new Thickness(0, 4), HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new TextBlock { Text = Wheels[i].Name, Classes = { "label" }, HorizontalAlignment = HorizontalAlignment.Center });
            var wheel = new ColorWheel { Width = 112, Height = 112, Margin = new Thickness(0, 4) };
            var valueText = new TextBlock { Classes = { "label" }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            wheel.ValueChanged += (hue, sat) =>
            {
                Wheels[index].Set(Target, Wheels[index].Get(Target) with { Hue = hue, Saturation = sat });
                ShowWheelValue(index);
                OnSettingsEdited();
            };
            _wheels[i] = wheel;
            _wheelValues[i] = valueText;
            cell.Children.Add(wheel);
            cell.Children.Add(valueText);
            Grid.SetColumn(cell, i % 2);
            Grid.SetRow(cell, i / 2);
            grid.Children.Add(cell);
        }
        body.Children.Add(grid);

        AddSlider(body, new("Яскравість тіней", -100, 100, Signed, s => s.GradeShadows.Luminance,
            (s, v) => s.GradeShadows = s.GradeShadows with { Luminance = v }));
        AddSlider(body, new("Яскравість середніх", -100, 100, Signed, s => s.GradeMidtones.Luminance,
            (s, v) => s.GradeMidtones = s.GradeMidtones with { Luminance = v }));
        AddSlider(body, new("Яскравість світлих", -100, 100, Signed, s => s.GradeHighlights.Luminance,
            (s, v) => s.GradeHighlights = s.GradeHighlights with { Luminance = v }));
        AddSlider(body, new("Загальна яскравість", -100, 100, Signed, s => s.GradeGlobal.Luminance,
            (s, v) => s.GradeGlobal = s.GradeGlobal with { Luminance = v }));
        AddSlider(body, new("Змішування", 0, 100, "0", s => s.GradeBlending, (s, v) => s.GradeBlending = v, Default: 50));
        AddSlider(body, new("Баланс", -100, 100, Signed, s => s.GradeBalance, (s, v) => s.GradeBalance = v));
        body.Children.Add(Hint("Колесо: напрям — відтінок, відстань від центру — сила. Подвійний клік — скинути."));
    }

    private void ShowWheelValue(int index)
    {
        var w = Wheels[index].Get(Target);
        _wheelValues[index].Text = w.Saturation == 0 ? "—" : $"{w.Hue:0}° · {w.Saturation:0}";
    }

    // ── Синхронізація ──────────────────────────────────────────────────────────

    /// <summary>Показує поточні <see cref="_settings"/> в усіх елементах, не записуючи це в історію.</summary>
    private void SyncControls()
    {
        bool applying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            foreach (var (def, slider, showValue) in _sliders)
            {
                slider.Value = def.Get(def.GlobalOnly ? _settings : Target);
                showValue();
            }
            _curveEditor.SetCurve(GetCurve(Target, _curveChannel));
            for (int i = 0; i < Wheels.Length; i++)
            {
                if (_wheels[i] is null) continue; // панель ще будується
                var w = Wheels[i].Get(Target);
                _wheels[i].SetValue(w.Hue, w.Saturation);
                ShowWheelValue(i);
            }
            UpdateCropOverlay();
            RefreshMaskList();
            RefreshHeal();
            SyncLensProfile();
        }
        finally
        {
            _applyingSettings = applying;
        }
    }
}
