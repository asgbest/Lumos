using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Розділ «🔭 Розмиття об'єктива»: сила, глибина різкої зони, боке і точка фокуса (клік по фото).
/// Карту глибини ШІ будує сам, щойно силу вперше зрушили з нуля; фокус за замовчуванням — на головному об'єкті.
/// </summary>
public partial class MainWindow
{
    private const string LensBlurSection = "🔭 Розмиття об'єктива";

    private ToggleButton _focusPick = null!;
    private bool _depthBusy;

    private void BuildLensBlurSection()
    {
        var body = AddSection(LensBlurSection);
        AddSlider(body, new("Сила", 0, 100, "0", s => s.BlurAmount, (s, v) => s.BlurAmount = v, GlobalOnly: true));
        AddSlider(body, new("Глибина різкості", 0, 100, "0", s => s.BlurRange, (s, v) => s.BlurRange = v, Default: 25, GlobalOnly: true));
        AddSlider(body, new("Боке (яскраві точки)", 0, 100, "0", s => s.BlurBokeh, (s, v) => s.BlurBokeh = v, Default: 50, GlobalOnly: true));

        _focusPick = new ToggleButton
        {
            Content = "🎯 Вибрати фокус (клік по фото)", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Classes = { "tool" },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_focusPick, "Клікни по тому, що має лишитися різким; що ближче чи далі — розмивається");
        _focusPick.IsCheckedChanged += (_, _) =>
        {
            if (_focusPick.IsChecked == true)
            {
                if (_healMode) SetHealMode(false);
                if (_maskIndex >= 0) SelectMask(-1);
                if (_cropMode) SetCropMode(false);
                if (_zoomed) SetZoom(false, null);
                SplitToggle.IsChecked = false;
            }
            UpdateFocusOverlay();
        };
        body.Children.Add(_focusPick);
        body.Children.Add(Hint("Карту глибини ШІ будує сам, коли вперше зрушиш «Силу» (кілька секунд). " +
                               "Фокус спершу — на головному об'єкті кадру; клікни «Вибрати фокус», щоб змінити."));

        FocusView.FocusPicked += (u, v) =>
        {
            CommitEdits();
            (_settings.BlurFocusX, _settings.BlurFocusY) = (u, v);
            _focusPick.IsChecked = false;
            OnSettingsEdited();
            ActionText.Text = "🎯 Фокус перенесено";
        };
    }

    /// <summary>Сила розмиття є, а карти глибини ще немає — збудувати (один раз на фото, у фоні).</summary>
    private void EnsureDepthMap()
    {
        if (_settings.BlurAmount > 0 && _settings.DepthMap is null && !_depthBusy) _ = BuildDepthMapAsync();
    }

    private async Task BuildDepthMapAsync()
    {
        if (_photoPath is not { } path || _preview is not { } preview) return;
        _depthBusy = true;
        try
        {
            if (!AiModels.IsInstalled(AiModels.Depth) && await new ModelDownloadDialog(AiModels.Depth).ShowDialog<bool>(this) != true) return;
            if (_photoPath != path) return;
            ActionText.Text = "🔭 Будую карту глибини…";
            var settings = _settings.Copy();
            var profile = _profile;
            var (depth, focus) = await Task.Run(() =>
            {
                var (bgra, w, h) = AiLooks.RenderAsShot(path, preview, settings, profile, withHeals: true);
                return (LensBlur.EstimateDepth(bgra, w, h), LensBlur.SuggestFocus(bgra, w, h));
            });
            if (_photoPath != path) return;
            _settings.DepthMap = depth.Raster;
            if (_settings.BlurFocusX < 0 && focus is { } f) (_settings.BlurFocusX, _settings.BlurFocusY) = f;
            OnSettingsEdited();
            ActionText.Text = $"🔭 Карта глибини готова за {depth.Milliseconds / 1000.0:0.0} с {(depth.Gpu ? "на відеокарті" : "на процесорі")}" +
                              (focus is null ? " — фокус на найближчому; клікни «Вибрати фокус», щоб змінити" : " — фокус на головному об'єкті");
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidOperationException)
        {
            ActionText.Text = $"⚠️ Не вдалося збудувати карту глибини: {ex.Message}";
        }
        finally
        {
            _depthBusy = false;
        }
    }

    /// <summary>Вибір фокуса — лише в звичайному перегляді.</summary>
    private void UpdateFocusOverlay()
    {
        if (_focusPick is null) return;
        bool show = _focusPick.IsChecked == true && !_cropMode && !_zoomed && SplitToggle.IsChecked != true && _preview is not null;
        FocusView.IsVisible = show;
        if (!show || _preview is null) return;
        var (w, h) = ImageGeometry.OutputSize(_preview.Width, _preview.Height, _settings, crop: true);
        FocusView.Update(_settings, w, h);
    }
}
