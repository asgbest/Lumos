using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumos.Core;
using System.Runtime.InteropServices;

namespace Lumos.App;

public partial class MainWindow : Window
{
    /// <summary>Довша сторона копії для редагування: повзунки рахуються по ній, повна роздільність — лише для експорту.</summary>
    private const int PreviewSide = 2048;

    private DevelopSettings _settings = new();

    private Photo? _photo;           // поточне фото (Image — прев'ю)
    private int _openGeneration;     // номер останнього відкриття: старі, ще не завершені, — ігноруються
    private Task<LinearImage>? _fullTask;
    private LinearImage? _preview;
    private LinearImage? _full;      // повна роздільність — для масштабу 100 % (довантажується у фоні)
    private double _previewScale = 1; // пікселів прев'ю на піксель оригіналу
    private int _fullLong = 1;
    private PhotoRenderCache _previewCache = new(), _zoomCache = new(), _beforeCache = new();
    private ToneProfile _profile = ToneProfile.Raw;
    private WriteableBitmap? _bitmap;
    private byte[] _buffer = [];
    private readonly int[] _histogram = new int[Developer.HistogramBins * 3];
    private bool _rendering, _dirty, _gpuWarned;
    private bool _cropMode;

    // Геометрія (поворот/вирівнювання/обрізка) перераховується, лише коли змінилась вона сама, а не на кожен рух повзунка тону.
    private readonly record struct GeometryKey(LinearImage Source, int Rotation, bool FlipH, WarpParams Warp, double X, double Y, double W, double H,
        EquatableList<HealSpot> Heals, (double, double, double, double, double, string?) Blur)
    {
        public static (double, double, double, double, double, string?) BlurOf(DevelopSettings s) =>
            (s.BlurAmount, s.BlurRange, s.BlurBokeh, s.BlurFocusX, s.BlurFocusY, s.DepthMap);
    }
    private GeometryKey? _geometryKey;
    private LinearImage? _geometryImage;

    private string? _photoPath;
    private readonly UiState _ui = UiState.Load();

    // Історія для Ctrl+Z: знімок правок записується, коли повзунок відпустили (пауза 400 мс без змін),
    // а не на кожен піксель руху — інакше один рух повзунка займав би сотні кроків.
    private List<DevelopSettings> _history = [new()];
    private int _historyIndex;
    private bool _applyingSettings;
    private readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public MainWindow() : this(null) { }

    public MainWindow(string? startupFile)
    {
        InitializeComponent();
        BuildPanels();

        // «До» і «Поруч» — взаємовиключні; «Поруч» не працює разом з обрізкою й масштабом 100 %.
        BeforeToggle.IsCheckedChanged += (_, _) =>
        {
            if (BeforeToggle.IsChecked == true) SplitToggle.IsChecked = false;
            RequestRender();
        };
        SplitToggle.IsCheckedChanged += (_, _) =>
        {
            if (SplitToggle.IsChecked == true)
            {
                BeforeToggle.IsChecked = false;
                if (_cropMode) SetCropMode(false);
                if (_zoomed) SetZoom(false, null);
            }
            else
            {
                SplitLabels.IsVisible = false;
            }
            RequestRender();
        };
        CropToggle.IsCheckedChanged += (_, _) => SetCropMode(CropToggle.IsChecked == true);
        CropView.CropChanged += OnCropDragged;
        InitZoom();
        InitLibrary();
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Без фокусу ніде Avalonia не передає натискання клавіш, тож вікно саме тримає фокус, коли поля не активні.
        Focusable = true;
        Opened += (_, _) => Focus();
        KeyDown += OnKeyDown;
        _commitTimer.Tick += (_, _) => CommitEdits();
        Closing += (_, _) => CommitEdits();

        // Файл із командного рядка — відкрити його (разом із його папкою); інакше — продовжити з минулого разу.
        if (startupFile is not null)
            Opened += async (_, _) => await OpenAsync(startupFile);
        else if (_ui.LastFolder is { } last && Directory.Exists(last))
            Opened += async (_, _) => await OpenFolderAsync(last, _ui.LastPhoto is { } p && File.Exists(p) ? p : null);
    }

    /// <summary>Перший запуск зібраного exe — пропозиція ярлика на робочому столі (з тим самим ui.json, що й вікно).</summary>
    public Task OfferDesktopShortcutAsync() => DesktopShortcut.OfferAsync(this, _ui);

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Відкрити фото",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Усі фото") { Patterns = [.. RawDecoder.Extensions.Concat(PhotoDecoder.RenderedExtensions).Select(Pattern)] },
                new FilePickerFileType("RAW") { Patterns = [.. RawDecoder.Extensions.Select(Pattern)] },
                new FilePickerFileType("JPEG, PNG, TIFF, HEIC, WebP") { Patterns = [.. PhotoDecoder.RenderedExtensions.Select(Pattern)] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await OpenAsync(path);

        static string Pattern(string extension) => "*." + extension;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        if (Directory.Exists(path)) await OpenFolderAsync(path, null);
        else await OpenAsync(path);
    }

    private async Task OpenAsync(string path)
    {
        path = Path.GetFullPath(path);
        // Фото з іншої папки — спершу відкриваємо його папку в бібліотеці (вона сама відкриє це фото).
        if (!InCurrentFolder(path))
        {
            await OpenFolderAsync(Path.GetDirectoryName(path)!, path);
            return;
        }

        CommitEdits(); // правки попереднього фото
        int generation = ++_openGeneration;
        _photoPath = null;   // поки нове не відкрилось, правки нікуди не пишуться
        _preview = null;
        _full = null;
        _fullTask = null;
        FileNameText.Text = Path.GetFileName(path);
        MarkCurrent(ItemFor(path));
        _ = ShowQuickPreviewAsync(path, generation);
        try
        {
            var photo = await LoadPreviewAsync(path);
            if (generation != _openGeneration) return; // поки чекали, перейшли до іншого фото
            var preview = photo.Image;

            SetZoom(false, null);
            _photo = photo;
            _preview = preview;
            _previewScale = preview.Width / (double)photo.FullWidth;
            _fullLong = Math.Max(photo.FullWidth, photo.FullHeight);
            StartFullLoad(path, generation);
            _previewCache = new PhotoRenderCache();
            _zoomCache = new PhotoRenderCache();
            _beforeCache = new PhotoRenderCache();
            _profile = photo.Profile;
            _bitmap = null;
            _geometryKey = null;
            _geometryImage = null;
            _fullGeometryKey = null;
            _fullGeometry = null;
            _regionKey = null;
            _region = null;
            // Швидкий показ (вбудований JPEG) лишається на екрані, доки перший кадр не порахується.

            FileNameText.Text = Path.GetFileName(path);
            Title = $"{Path.GetFileName(path)} — Lumos";
            CameraText.Text = string.IsNullOrEmpty(photo.Lens) ? photo.Camera : $"{photo.Camera} · {photo.Lens}";
            ShotText.Text = string.Join(" · ", new[] { photo.Exposure, $"{photo.FullWidth}×{photo.FullHeight}" }.Where(s => s.Length > 0));
            StatusText.IsVisible = false;
            ActionText.Text = "";

            // Збережені раніше правки цього фото (файл .lumos.json поруч) — одразу на повзунки.
            var saved = EditSidecar.Load(path);
            _photoPath = path;
            _history = [saved];
            _historyIndex = 0;
            CropToggle.IsChecked = false;
            SetAspectChoice(0);
            _maskIndex = -1; // нове фото — правки знову для всього кадру
            _healIndex = -1;
            ApplySettings(saved);
            if (EditSidecar.Exists(path)) ActionText.Text = "Відновлено збережені правки";

            foreach (var body in _sectionBodies) body.IsEnabled = true;
            ZoomToggle.IsEnabled = CropToggle.IsEnabled = BeforeToggle.IsEnabled = SplitToggle.IsEnabled = ResetButton.IsEnabled = ExportButton.IsEnabled = AiButton.IsEnabled = true;
            UpdateHistoryButtons();
            RequestRender();

            _ui.LastPhoto = path;
            _ui.Save();
            PrefetchNeighbours();
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration) return;
            PhotoView.Source = null;
            StatusText.Text = $"Не вдалося відкрити {Path.GetFileName(path)}:\n{ex.Message}";
            StatusText.IsVisible = true;
        }
    }

    /// <summary>Повна роздільність (для масштабу 100 %) — у фоні, трохи згодом: якщо швидко гортати фото, вона не рахується дарма.</summary>
    private void StartFullLoad(string path, int generation)
    {
        _fullTask = Task.Run(async () =>
        {
            await Task.Delay(700);
            if (generation != _openGeneration) throw new OperationCanceledException();
            return PhotoDecoder.Load(path).Image;
        });
        _fullTask.ContinueWith(t =>
        {
            if (generation != _openGeneration || t.Status != TaskStatus.RanToCompletion) return;
            _full = t.Result;
            if (_zoomed) RequestRender();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_photoPath is null) return;
        SetAspectChoice(0);
        ApplySettings(DevelopSettings.DefaultFor(_photoPath));
        OnSettingsEdited();
    }

    /// <summary>Ставить усі елементи на задані правки, не записуючи це як нову дію в історію.</summary>
    private void ApplySettings(DevelopSettings settings)
    {
        _settings = settings.Copy();
        SyncControls();
        RequestRender();
    }

    /// <summary>Користувач щось змінив: перерахувати фото й (після паузи) записати крок історії та файл правок.</summary>
    private void OnSettingsEdited()
    {
        EnsureDepthMap();
        RequestRender();
        if (_applyingSettings) return;
        _commitTimer.Stop();
        _commitTimer.Start();
        UpdateHistoryButtons();
    }

    /// <summary>Фіксує поточні правки: новий крок історії (якщо щось змінилося) і запис у файл поруч із фото.</summary>
    private void CommitEdits()
    {
        _commitTimer.Stop();
        if (_photoPath is null) return;

        var current = _settings.Copy();
        if (current == _history[_historyIndex]) return;

        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1); // після Ctrl+Z нова дія стирає «майбутнє»
        _history.Add(current);
        _historyIndex++;
        SaveEdits();
        UpdateHistoryButtons();
    }

    private void SaveEdits()
    {
        if (_photoPath is null) return;
        try
        {
            EditSidecar.Save(_photoPath, _settings.Copy());
            UpdateCurrentThumbnail();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"⚠️ Не вдалося зберегти правки: {ex.Message}";
        }
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e) => StepHistory(-1);
    private void OnRedoClick(object? sender, RoutedEventArgs e) => StepHistory(+1);

    private void StepHistory(int direction)
    {
        CommitEdits(); // незафіксований рух повзунка — теж крок, який можна скасувати
        int target = _historyIndex + direction;
        if (_photoPath is null || target < 0 || target >= _history.Count) return;

        _historyIndex = target;
        ApplySettings(_history[target]);
        SaveEdits();
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        bool pending = _photoPath is not null && _settings != _history[_historyIndex];
        UndoButton.IsEnabled = _historyIndex > 0 || pending;
        RedoButton.IsEnabled = !pending && _historyIndex < _history.Count - 1;
    }

    /// <summary>Режим обрізки: фото показується повністю (з вирівнюванням), поверх — рамка, яку можна тягнути.</summary>
    private void SetCropMode(bool on)
    {
        if (_cropMode == on) return;
        if (on && _zoomed) SetZoom(false, null); // рамку тягнемо на всьому кадрі
        if (on) SplitToggle.IsChecked = false;
        _cropMode = on;
        CropToggle.IsChecked = on;
        CropView.IsVisible = on;
        if (on && _sectionCollapse.TryGetValue(CropSection, out var setCollapsed)) setCollapsed(false);
        UpdateCropOverlay();
        UpdateMaskOverlay();
        RequestRender();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e) => await ExportAsync();

    private async void OnAiClick(object? sender, RoutedEventArgs e) => await OpenAiStylistAsync();

    /// <summary>«✨ AI»: вікно з варіантами від Gemini; вибраний варіант замінює правки фото (окремий крок історії).</summary>
    private async Task OpenAiStylistAsync()
    {
        if (_photoPath is not { } path || _photo is not { } photo || _preview is not { } preview) return;
        CommitEdits();
        var context = new AiStyleContext(photo, path, preview, _profile, _settings.Copy(), _ui, (settings, name) =>
        {
            if (_photoPath != path) return; // поки вікно було відкрите, перейшли на інше фото
            CommitEdits();
            if (_healMode) SetHealMode(false);
            _maskIndex = -1;
            ApplySettings(settings);
            CommitEdits();
            ActionText.Text = $"✨ Стиль «{name}» від Gemini застосовано (Ctrl+Z — скасувати)";
        });
        await new AiStyleDialog(context).ShowDialog(this);
    }

    private async Task ExportAsync()
    {
        if (_photoPath is not { } photoPath || !ExportButton.IsEnabled) return;
        CommitEdits();
        if (LibraryList.SelectedItems?.Count > 1)
        {
            await ExportManyAsync(SelectedOrCurrent());
            return;
        }

        // Якщо оригінал сам JPEG, пропонуємо інше ім'я, щоб випадково не записати поверх нього.
        string baseName = Path.GetFileNameWithoutExtension(photoPath);
        bool sourceIsJpeg = Path.GetExtension(photoPath).ToLowerInvariant() is ".jpg" or ".jpeg";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Експорт у JPEG",
            SuggestedFileName = baseName + (sourceIsJpeg ? "-Lumos.jpg" : ".jpg"),
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(photoPath)!),
            DefaultExtension = "jpg",
            FileTypeChoices = [FilePickerFileTypes.ImageJpg],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } jpegPath) return;
        if (string.Equals(Path.GetFullPath(jpegPath), Path.GetFullPath(photoPath), StringComparison.OrdinalIgnoreCase))
        {
            ActionText.Text = "⚠️ Не можна записати експорт поверх оригіналу — обери інше ім'я";
            return;
        }

        ExportButton.IsEnabled = false;
        ActionText.Text = $"Експортую {Path.GetFileName(jpegPath)}…";
        try
        {
            var settings = _settings.Copy();
            await Task.Run(() => JpegExporter.Export(photoPath, settings, jpegPath));
            ActionText.Text = $"✅ Збережено {Path.GetFileName(jpegPath)}";
        }
        catch (Exception ex)
        {
            ActionText.Text = $"⚠️ Експорт не вдався: {ex.Message}";
        }
        finally
        {
            ExportButton.IsEnabled = _photoPath is not null;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // Поки вписують число в поле, звичайні літери — це текст, а не команди.
        bool typing = FocusManager?.GetFocusedElement() is TextBox;

        if (ctrl && shift && e.Key == Key.O) _ = PickFolderAsync();
        else if (ctrl && e.Key == Key.O) _ = PickAndOpenAsync();
        else if (ctrl && e.Key == Key.E) _ = ExportAsync();
        else if (ctrl && shift && e.Key == Key.C) CopySettings();
        else if (ctrl && shift && e.Key == Key.V) PasteSettings();
        else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) StepHistory(+1);
        else if (ctrl && e.Key == Key.Z) StepHistory(-1);
        else if (typing || ctrl) return;
        else if (e.Key == Key.R && CropToggle.IsEnabled) SetCropMode(!_cropMode);
        else if (e.Key == Key.Z && ZoomToggle.IsEnabled) SetZoom(!_zoomed, null);
        else if (e.Key is Key.Enter or Key.Escape && _cropMode) SetCropMode(false);
        // Відновлення: H — увімкнути, Delete — прибрати вибрану латку, [ ] — розмір, Esc — зняти вибір, потім вийти.
        else if (e.Key == Key.H && _photoPath is not null) SetHealMode(!_healMode);
        else if (e.Key == Key.A && AiButton.IsEnabled) _ = OpenAiStylistAsync();
        else if (e.Key is Key.Delete or Key.Back && _healMode) DeleteSelectedHeal();
        else if (e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets && _healMode) ChangeHealSize(e.Key == Key.OemCloseBrackets ? 2 : -2);
        else if (e.Key == Key.Escape && _healMode)
        {
            if (_healIndex >= 0) { _healIndex = -1; RefreshHeal(); }
            else SetHealMode(false);
        }
        // Маски: Esc — назад до всього фото, O — підсвітити маску, [ ] — розмір пензля.
        else if (e.Key == Key.Escape && _rangePick != RangePick.None) SetRangePick(RangePick.None); // спершу — вийти з піпетки
        else if (e.Key == Key.Escape && _maskIndex >= 0) SelectMask(-1);
        else if (e.Key == Key.O && _maskIndex >= 0) _overlayBox.IsChecked = !_overlayBox.IsChecked;
        else if (e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets && _maskIndex >= 0) ChangeBrushSize(e.Key == Key.OemCloseBrackets ? 2 : -2);
        else if (e.Key is Key.OemBackslash or Key.Oem5 && BeforeToggle.IsEnabled) BeforeToggle.IsChecked = !BeforeToggle.IsChecked;
        else if (e.Key == Key.Y && SplitToggle.IsEnabled) SplitToggle.IsChecked = !SplitToggle.IsChecked;
        // Бібліотека: як у Lightroom — стрілки, G (сітка), 0–5 (зірочки), P / X / U (вибрано / відхилено / зняти).
        else if (e.Key is Key.Right or Key.Left && _allItems.Count > 0) Navigate(e.Key == Key.Right ? 1 : -1);
        else if (e.Key == Key.G && GridToggle.IsEnabled) SetGridMode(!_gridMode);
        else if (e.Key is >= Key.D0 and <= Key.D5 && _allItems.Count > 0) SetRating(e.Key - Key.D0);
        else if (e.Key is >= Key.NumPad0 and <= Key.NumPad5 && _allItems.Count > 0) SetRating(e.Key - Key.NumPad0);
        else if (e.Key == Key.P && _allItems.Count > 0) SetFlag(PhotoFlag.Pick);
        else if (e.Key == Key.X && _allItems.Count > 0) SetFlag(PhotoFlag.Reject);
        else if (e.Key == Key.U && _allItems.Count > 0) SetFlag(PhotoFlag.None);
        else return;
        e.Handled = true;
    }

    /// <summary>
    /// Перерахунок без черги: поки йде один кадр, нові зміни лише позначаються, і після нього рахується останній стан.
    /// Так повзунок не «накопичує» відставання, навіть якщо його швидко тягнути.
    /// </summary>
    private async void RequestRender()
    {
        if (_preview is null) return;
        _dirty = true;
        if (_rendering) return;

        _rendering = true;
        try
        {
            while (_dirty && _preview is { } preview)
            {
                _dirty = false;
                var after = _hoverStyle is { } hover
                    ? ApplyStyleTo(_settings, hover, _styleAmount) // попередній перегляд стилю під мишкою
                    : _settings.Copy();
                var before = BeforeSettings();
                var settings = BeforeToggle.IsChecked == true ? before : after;
                var profile = _profile;
                bool cropMode = _cropMode;
                bool zoomed = _zoomed && !cropMode && _full is not null;
                bool split = SplitToggle.IsChecked == true && !cropMode && !zoomed;
                int highlight = _showMaskOverlay && !split ? _maskIndex : -1; // підсвітити вибрану маску червоним
                var full = _full;
                var (centerX, centerY) = (_zoomX, _zoomY);
                var (viewW, viewH) = ZoomViewport();
                double previewScale = _previewScale;
                int fullLong = _fullLong;

                var (buffer, width, height) = await Task.Run(() =>
                {
                    if (zoomed) return RenderZoomed(full!, settings, profile, centerX, centerY, viewW, viewH, highlight);
                    var img = GetGeometry(preview, settings, cropMode);
                    var frame = RenderFrame.For(img, previewScale, fullLong);
                    int size = img.Width * img.Height * 4;
                    var buf = _buffer.Length == size ? _buffer : new byte[size];
                    PhotoRenderer.Render(img, split ? after : settings, profile, buf, _histogram, frame, _previewCache, highlight);
                    if (!split) return (buf, img.Width, img.Height);

                    // «Поруч»: той самий кадр без правок ліворуч, із правками праворуч, між ними — смужка фону.
                    var beforeBuf = new byte[size];
                    PhotoRenderer.Render(img, before, profile, beforeBuf, new int[Developer.HistogramBins * 3], frame, _beforeCache);
                    return SideBySide(beforeBuf, buf, img.Width, img.Height);
                });
                if (preview != _preview) continue; // поки рахували, відкрили інше фото

                if (!zoomed && !split) _buffer = buffer;
                SplitLabels.IsVisible = split;
                // У масштабі 100 % піксель зображення = піксель екрана, тож роздільність бітмапа враховує масштаб Windows.
                double dpi = zoomed ? 96 * RenderScaling : 96;
                if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height || _bitmap.Dpi.X != dpi)
                {
                    _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(dpi, dpi),
                        PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    PhotoView.Source = _bitmap;
                }
                using (var fb = _bitmap.Lock())
                {
                    int rowBytes = width * 4;
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(buffer, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
                }
                PhotoView.InvalidateVisual();
                // Відеокарта відмовила посеред роботи — програма вже перейшла на процесор; кажемо про це один раз.
                if (!_gpuWarned && OperatingSystem.IsWindowsVersionAtLeast(6, 2) && Core.Gpu.GpuContext.LastError is { } gpuError)
                {
                    _gpuWarned = true;
                    ActionText.Text = $"⚠️ Відеокарта відмовила — обробка йде на процесорі ({gpuError})";
                }
                UpdateMaskOverlay(); // розмір кадру міг змінитись (обрізка) — рамка маски має збігатися з фото
                if (!zoomed) // гістограма — для всього кадру, а не для видимого шматка
                {
                    Histogram.Update(_histogram);
                    UpdateCurveHistogram();
                }
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>
    /// «До»: стартові правки фото (для RAW — різкість і шумозаглушення, як після імпорту), але з тією самою обрізкою й поворотом,
    /// щоб порівнювати один і той самий кадр.
    /// </summary>
    private DevelopSettings BeforeSettings()
    {
        var before = DevelopSettings.DefaultFor(_photoPath ?? "");
        before.CopyGeometryFrom(_settings);
        (before.CropX, before.CropY, before.CropW, before.CropH) = (_settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);
        return before;
    }

    private const int SplitGap = 16;

    private static (byte[] Pixels, int Width, int Height) SideBySide(byte[] left, byte[] right, int w, int h)
    {
        int width = w * 2 + SplitGap, row = w * 4;
        var result = new byte[width * h * 4];
        for (int i = 0; i < result.Length; i += 4)
        {
            result[i] = result[i + 1] = result[i + 2] = 0x12; // колір фону області фото (#121212)
            result[i + 3] = 255;
        }
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(left, y * row, result, y * width * 4, row);
            Buffer.BlockCopy(right, y * row, result, (y * width + w + SplitGap) * 4, row);
        }
        return (result, width, h);
    }

    /// <summary>Повернуте й обрізане прев'ю; у режимі обрізки — усе полотно без обрізки, щоб було видно, що відрізається.</summary>
    private LinearImage GetGeometry(LinearImage preview, DevelopSettings s, bool cropMode)
    {
        bool crop = !cropMode;
        var key = new GeometryKey(preview, s.Rotation, s.FlipH, WarpParams.Of(s),
            crop ? s.CropX : 0, crop ? s.CropY : 0, crop ? s.CropW : 1, crop ? s.CropH : 1, s.Heals, GeometryKey.BlurOf(s));
        if (key != _geometryKey || _geometryImage is null)
        {
            _geometryImage = ImageGeometry.Apply(preview, s, crop);
            _geometryKey = key;
        }
        return _geometryImage;
    }
}
