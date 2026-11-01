using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Маски за кольором і яскравістю (як «Range Mask» у Lightroom): окремі маски на весь кадр («🎨 Колір», «☀️ Яскравість»)
/// і уточнення будь-якої іншої маски — напр. радіальна, але лише по блакитному небу. Колір береться піпеткою з фото
/// (Shift+клік — додати ще один, до п'яти), яскравість — піпеткою або повзунками меж.
/// </summary>
public partial class MainWindow
{
    private enum RangePick { None, Color, Light }

    private const int MaxRangeColors = 5;

    private RangePick _rangePick;
    private StackPanel _rangeOptions = null!, _rangeColorBlock = null!, _rangeLightBlock = null!;
    private TextBlock _rangeHeader = null!, _rangeColorInfo = null!;
    private ToggleButton _pickColorButton = null!, _pickLightButton = null!;
    private Button _clearColorsButton = null!;
    private Slider _rangeAmount = null!, _rangeMin = null!, _rangeMax = null!, _rangeSmooth = null!;

    private void BuildRangeOptions(Panel parent)
    {
        _rangeOptions = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _rangeHeader = new TextBlock { Classes = { "label" }, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        _rangeOptions.Children.Add(_rangeHeader);

        _rangeColorBlock = new StackPanel();
        _pickColorButton = PickButton("🎯 Піпетка кольору", "Клікни на фото колір, який має діяти (Shift+клік — додати ще один)", RangePick.Color);
        _clearColorsButton = SmallButton("Скинути", "Прибрати вибрані кольори", () =>
        {
            if (SelectedMask is { } m) ReplaceMask(m with { RangeColors = EquatableList<RangeColor>.Empty });
        });
        _clearColorsButton.Margin = new Thickness(6, 0, 0, 0);
        var colorRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_clearColorsButton, Dock.Right);
        colorRow.Children.Add(_clearColorsButton);
        colorRow.Children.Add(_pickColorButton);
        _rangeColorBlock.Children.Add(colorRow);
        _rangeColorInfo = new TextBlock { Classes = { "label" }, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        _rangeColorBlock.Children.Add(_rangeColorInfo);
        _rangeAmount = PlainSlider(_rangeColorBlock, "Допуск кольору", 0, 100, 50, "0", v => ChangeRange(m => m.RangeColorAmount == v ? null : m with { RangeColorAmount = v }), resetTo: 50);
        _rangeOptions.Children.Add(_rangeColorBlock);

        _rangeLightBlock = new StackPanel();
        _pickLightButton = PickButton("🎯 Піпетка яскравості", "Клікни на фото — межі стануть довкола його яскравості", RangePick.Light);
        _pickLightButton.Margin = new Thickness(0, 8, 0, 0);
        _rangeLightBlock.Children.Add(_pickLightButton);
        _rangeMin = PlainSlider(_rangeLightBlock, "Яскравість від", 0, 100, 0, "0", v =>
            ChangeRange(m => m.RangeLumMin == v ? null : m with { RangeLumMin = v, RangeLumMax = Math.Max(m.RangeLumMax, v) }), resetTo: 0);
        _rangeMax = PlainSlider(_rangeLightBlock, "Яскравість до", 0, 100, 100, "0", v =>
            ChangeRange(m => m.RangeLumMax == v ? null : m with { RangeLumMax = v, RangeLumMin = Math.Min(m.RangeLumMin, v) }), resetTo: 100);
        _rangeSmooth = PlainSlider(_rangeLightBlock, "М'якість переходу", 0, 100, 50, "0", v =>
            ChangeRange(m => m.RangeLumSmooth == v ? null : m with { RangeLumSmooth = v }), resetTo: 50);
        _rangeOptions.Children.Add(_rangeLightBlock);
        parent.Children.Add(_rangeOptions);

        MaskView.Picked += (point, add) => _ = PickRangeAsync(point, add);
    }

    private ToggleButton PickButton(string text, string tip, RangePick mode)
    {
        var button = new ToggleButton
        {
            Content = text, FontSize = 12, Padding = new Thickness(8, 3), Classes = { "tool" },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.IsCheckedChanged += (_, _) =>
        {
            if (_syncingMaskOptions) return;
            SetRangePick(button.IsChecked == true ? mode : _rangePick == mode ? RangePick.None : _rangePick);
        };
        return button;
    }

    private void SetRangePick(RangePick mode)
    {
        _rangePick = mode;
        bool syncing = _syncingMaskOptions;
        _syncingMaskOptions = true;
        _pickColorButton.IsChecked = mode == RangePick.Color;
        _pickLightButton.IsChecked = mode == RangePick.Light;
        _syncingMaskOptions = syncing;
        if (mode != RangePick.None)
            ActionText.Text = mode == RangePick.Color
                ? "🎯 Клікни на фото колір для маски (Shift+клік — додати ще один)"
                : "🎯 Клікни на фото — маска візьме таку саму яскравість";
        UpdateMaskOverlay();
    }

    /// <summary>Зміна налаштувань діапазону з повзунка; null — нічого не змінилось.</summary>
    private void ChangeRange(Func<Mask, Mask?> change)
    {
        if (_applyingSettings || _syncingMaskOptions || SelectedMask is not { } m) return;
        if (change(m) is { } next) ReplaceMask(next);
    }

    /// <summary>Піпетка: колір чи яскравість у точці фото (як «бачить» маска — до правок, з експозицією).</summary>
    private async Task PickRangeAsync(MaskPoint point, bool add)
    {
        if (SelectedMask is not { } mask || _rangePick == RangePick.None || _preview is not { } preview) return;
        var settings = _settings.Copy();
        var image = await Task.Run(() => GetGeometry(preview, settings, cropMode: false));
        int x = (int)((point.X - settings.CropX) / Math.Max(1e-6, settings.CropW) * image.Width);
        int y = (int)((point.Y - settings.CropY) / Math.Max(1e-6, settings.CropH) * image.Height);
        if (RangeMask.Sample(image, x, y, RangeLook.For(settings, _profile)) is not { } sample || SelectedMask != mask) return;

        CommitEdits();
        if (_rangePick == RangePick.Color)
        {
            var colors = add && mask.RangeColors.Count < MaxRangeColors ? mask.RangeColors.Add(sample.Color) : new([sample.Color]);
            ReplaceMask(mask with { RangeColors = colors });
            ActionText.Text = colors.Count > 1 ? $"🎨 Кольорів у масці: {colors.Count}" : "🎨 Колір вибрано. Shift+клік — додати ще один";
        }
        else
        {
            double l = Math.Round(sample.Lightness);
            ReplaceMask(mask with { RangeLumMin = Math.Max(0, l - 15), RangeLumMax = Math.Min(100, l + 15) });
            ActionText.Text = $"☀️ Яскравість {l:0}: межі {Math.Max(0, l - 15):0}…{Math.Min(100, l + 15):0}";
        }
        if (!_showMaskOverlay) _overlayBox.IsChecked = true; // одразу видно, що потрапило в маску
    }

    /// <summary>Блоки діапазону — під вибрану маску: «Колір» — лише колір, «Яскравість» — лише яскравість, інші — обидва.</summary>
    private void SyncRangeOptions(Mask? mask)
    {
        if (_rangeOptions is null) return;
        _rangeOptions.IsVisible = mask is not null;
        if (mask is null)
        {
            if (_rangePick != RangePick.None) SetRangePick(RangePick.None);
            return;
        }
        _rangeHeader.Text = mask.Kind switch
        {
            MaskKind.ColorRange => "🎨 Колір",
            MaskKind.LuminanceRange => "☀️ Яскравість",
            _ => "Обмежити за кольором / яскравістю",
        };
        _rangeColorBlock.IsVisible = mask.Kind != MaskKind.LuminanceRange;
        _rangeLightBlock.IsVisible = mask.Kind != MaskKind.ColorRange;
        if (_rangePick == RangePick.Color && !_rangeColorBlock.IsVisible || _rangePick == RangePick.Light && !_rangeLightBlock.IsVisible)
            SetRangePick(RangePick.None);
        int n = mask.RangeColors.Count;
        _rangeColorInfo.Text = n == 0
            ? (mask.Kind == MaskKind.ColorRange ? "Колір ще не вибрано — маска на весь кадр" : "Колір не обмежено")
            : $"Кольорів: {n} (до {MaxRangeColors}; Shift+клік — додати)";
        _clearColorsButton.IsEnabled = n > 0;
        _rangeAmount.IsEnabled = n > 0;
        _rangeAmount.Value = mask.RangeColorAmount;
        _rangeMin.Value = mask.RangeLumMin;
        _rangeMax.Value = mask.RangeLumMax;
        _rangeSmooth.Value = mask.RangeLumSmooth;
    }
}
