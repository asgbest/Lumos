using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumos.Core;
using System.Runtime.InteropServices;
using Thumb = Lumos.Core.Thumb;

namespace Lumos.App;

/// <summary>
/// Бібліотека: папка з фото, стрічка/сітка мініатюр, рейтинги й позначки (каталог SQLite),
/// фільтр, перемикання між фото та пакетні дії (правки, стиль, експорт) над вибраними.
/// </summary>
public partial class MainWindow
{
    private const int ThumbSize = 320;

    private readonly ObservableCollection<PhotoItem> _items = [];
    private List<PhotoItem> _allItems = [];
    private string? _folder;
    private readonly Catalog _catalog = new(Catalog.DefaultPath);
    private CancellationTokenSource? _thumbsCts;
    private readonly Channel<PhotoItem> _editedThumbs = Channel.CreateUnbounded<PhotoItem>();
    private PhotoItem? _currentItem;
    private bool _syncingSelection;
    private bool _gridMode;
    private Style? _copiedSettings;

    /// <summary>Сусідні фото, розкодовані заздалегідь: перемикання на них — без очікування.</summary>
    private readonly Dictionary<string, Task<Photo>> _prefetch = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Label, Func<PhotoItem, bool> Match)[] Filters =
    [
        ("Усі фото", _ => true),
        ("Вибрані ⚑", i => i.Flag == PhotoFlag.Pick),
        ("Без відхилених", i => i.Flag != PhotoFlag.Reject),
        ("★ і вище", i => i.Rating >= 1),
        ("★★ і вище", i => i.Rating >= 2),
        ("★★★ і вище", i => i.Rating >= 3),
        ("★★★★ і вище", i => i.Rating >= 4),
        ("★★★★★", i => i.Rating == 5),
        ("З правками", i => i.Edited),
    ];

    private void InitLibrary()
    {
        LibraryList.ItemsSource = _items;
        FilterBox.ItemsSource = Filters.Select(f => f.Label).ToArray();
        FilterBox.SelectedIndex = 0;
        FilterBox.SelectionChanged += (_, _) => ApplyFilter();
        LibraryList.SelectionChanged += OnLibrarySelectionChanged;
        LibraryList.ContainerPrepared += (_, e) => e.Container.Classes.Set("current", e.Container.DataContext == _currentItem);
        LibraryList.DoubleTapped += (_, e) =>
        {
            if (!_gridMode || (e.Source as Control)?.DataContext is not PhotoItem item) return;
            SetGridMode(false);
            _ = OpenAsync(item.Path);
        };
        LibraryList.AddHandler(PointerWheelChangedEvent, OnStripWheel, RoutingStrategies.Tunnel);
        GridToggle.IsCheckedChanged += (_, _) => SetGridMode(GridToggle.IsChecked == true);
        _ = Task.Run(EditedThumbnailWorker);
        Closing += (_, _) => _catalog.Dispose();
    }

    // ── Папка ──────────────────────────────────────────────────────────────────

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка з фото",
            SuggestedStartLocation = _folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(_folder),
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await OpenFolderAsync(path, null);
    }

    /// <param name="select">Яке фото відкрити (null — перше).</param>
    private async Task OpenFolderAsync(string folder, string? select)
    {
        var files = await Task.Run(() => Directory.EnumerateFiles(folder)
            .Where(PhotoDecoder.IsSupported)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList());
        if (select is not null && !files.Contains(select, StringComparer.OrdinalIgnoreCase)) files.Add(select);
        if (files.Count == 0)
        {
            StatusText.Text = $"У папці «{Path.GetFileName(folder)}» немає фото.";
            StatusText.IsVisible = true;
            return;
        }

        _folder = Path.GetFullPath(folder);
        _ui.LastFolder = _folder;
        _ui.Save();
        var marks = _catalog.LoadFolder(_folder);
        _allItems = files.Select(f =>
        {
            var item = new PhotoItem(f) { Edited = EditSidecar.Exists(f) };
            if (marks.TryGetValue(f, out var mark)) (item.Rating, item.Flag) = mark;
            return item;
        }).ToList();
        _currentItem = null;
        _prefetch.Clear();
        ApplyFilter();
        LibraryPanel.IsVisible = true;
        GridToggle.IsEnabled = true;
        StartThumbnailLoading();

        await OpenAsync(select ?? files[0]);
    }

    private bool InCurrentFolder(string path) =>
        _folder is not null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _folder, StringComparison.OrdinalIgnoreCase);

    private PhotoItem? ItemFor(string path) =>
        _allItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    // ── Мініатюри ──────────────────────────────────────────────────────────────

    /// <summary>Мініатюри в кілька потоків: з правками — з кешу, інакше швидка (вбудований JPEG), а з правками — у чергу.</summary>
    private void StartThumbnailLoading()
    {
        _thumbsCts?.Cancel();
        var cts = _thumbsCts = new CancellationTokenSource();
        var items = _allItems.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token }, item =>
                {
                    Thumb? thumb = item.Edited ? Thumbnails.FromCache(item.Path, ThumbSize) : null;
                    bool needEdited = item.Edited && thumb is null;
                    thumb ??= Thumbnails.Fast(item.Path, ThumbSize);
                    if (thumb is not null) Dispatcher.UIThread.Post(() => item.Thumbnail = ToBitmap(thumb));
                    if (needEdited) _editedThumbs.Writer.TryWrite(item);
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Мініатюри з правками рахуються по одній у фоні: фото розкодовується, проходить увесь конвеєр, результат іде в кеш.</summary>
    private async Task EditedThumbnailWorker()
    {
        await foreach (var item in _editedThumbs.Reader.ReadAllAsync())
        {
            try
            {
                var settings = EditSidecar.Load(item.Path);
                Thumb? thumb;
                if (settings.IsDefaultFor(item.Path))
                {
                    thumb = Thumbnails.Fast(item.Path, ThumbSize);
                }
                else
                {
                    thumb = Thumbnails.Edited(LoadPreview(item.Path), settings, ThumbSize);
                    Thumbnails.SaveToCache(item.Path, ThumbSize, thumb);
                }
                if (thumb is not null) Dispatcher.UIThread.Post(() => item.Thumbnail = ToBitmap(thumb));
            }
            catch (Exception ex) when (ex is IOException or Sdcb.LibRaw.LibRawException or ImageMagick.MagickException) { }
        }
    }

    /// <summary>Після правки поточного фото його мініатюра перераховується з уже готового прев'ю — за мілісекунди.</summary>
    private void UpdateCurrentThumbnail()
    {
        if (_currentItem is not { } item || _photo is not { } photo) return;
        var settings = _settings.Copy();
        bool unedited = settings.IsDefaultFor(item.Path);
        item.Edited = !unedited;
        _ = Task.Run(() =>
        {
            Thumb? thumb = unedited ? Thumbnails.Fast(item.Path, ThumbSize) : Thumbnails.Edited(photo, settings, ThumbSize);
            if (!unedited && thumb is not null) Thumbnails.SaveToCache(item.Path, ThumbSize, thumb);
            if (thumb is not null) Dispatcher.UIThread.Post(() => item.Thumbnail = ToBitmap(thumb));
        });
    }

    internal static WriteableBitmap ToBitmap(Thumb thumb)
    {
        var bitmap = new WriteableBitmap(new PixelSize(thumb.Width, thumb.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        int row = thumb.Width * 4;
        for (int y = 0; y < thumb.Height; y++) Marshal.Copy(thumb.Bgra, y * row, fb.Address + y * fb.RowBytes, row);
        return bitmap;
    }

    // ── Швидке перемикання ─────────────────────────────────────────────────────

    /// <summary>Прев'ю для редагування: RAW — у половинній роздільності, зменшене до 2048; повний кадр — окремо, у фоні.</summary>
    private static Photo LoadPreview(string path)
    {
        var photo = PhotoDecoder.Load(path, forPreview: true);
        return photo with { Image = photo.Image.Downscale(PreviewSide) };
    }

    private Task<Photo> LoadPreviewAsync(string path) =>
        _prefetch.Remove(path, out var ready) ? ready : Task.Run(() => LoadPreview(path));

    /// <summary>Готує сусідні фото заздалегідь (і забуває решту, щоб не тримати зайву пам'ять).</summary>
    private void PrefetchNeighbours()
    {
        if (_currentItem is null) return;
        int index = _items.IndexOf(_currentItem);
        var wanted = new[] { index + 1, index - 1 }
            .Where(i => i >= 0 && i < _items.Count)
            .Select(i => _items[i].Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _prefetch.Keys.Where(k => !wanted.Contains(k)).ToList()) _prefetch.Remove(stale);
        foreach (var path in wanted)
            if (!_prefetch.ContainsKey(path)) _prefetch[path] = Task.Run(() => LoadPreview(path));
    }

    /// <summary>Поки справжнє фото розкодовується, показуємо вбудований JPEG — перемикання виглядає миттєвим.</summary>
    private async Task ShowQuickPreviewAsync(string path, int generation)
    {
        var thumb = await Task.Run(() => Thumbnails.Fast(path, PreviewSide));
        if (thumb is null || generation != _openGeneration || _preview is not null) return;
        _bitmap = null;
        PhotoView.Source = ToBitmap(thumb);
        StatusText.IsVisible = false;
    }

    private void Navigate(int step)
    {
        if (_items.Count == 0) return;
        int index = _currentItem is null ? 0 : _items.IndexOf(_currentItem) + step;
        if (index < 0 || index >= _items.Count) return;
        LibraryList.SelectedItem = _items[index]; // відкриє фото через SelectionChanged
        LibraryList.ScrollIntoView(_items[index]);
    }

    // ── Вибір і режими ─────────────────────────────────────────────────────────

    private void OnLibrarySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLibraryText();
        if (_syncingSelection || _gridMode) return;
        if (LibraryList.SelectedItems?.Count == 1 && LibraryList.SelectedItem is PhotoItem item && item != _currentItem)
            _ = OpenAsync(item.Path);
    }

    /// <summary>Позначає поточне фото в стрічці (рамка) і вибирає його, не відкриваючи вдруге.</summary>
    private void MarkCurrent(PhotoItem? item)
    {
        _currentItem = item;
        foreach (var container in LibraryList.GetRealizedContainers())
            container.Classes.Set("current", container.DataContext == item);
        if (item is null) return;
        var selected = LibraryList.SelectedItems;
        if (selected is null || !selected.Contains(item) || selected.Count == 1)
        {
            _syncingSelection = true;
            LibraryList.SelectedItem = item;
            _syncingSelection = false;
        }
        LibraryList.ScrollIntoView(item);
    }

    private List<PhotoItem> SelectedOrCurrent()
    {
        var selected = LibraryList.SelectedItems?.OfType<PhotoItem>().ToList() ?? [];
        if (selected.Count == 0 && _currentItem is not null) selected.Add(_currentItem);
        return selected;
    }

    private void SetGridMode(bool on)
    {
        if (_allItems.Count == 0) on = false;
        _gridMode = on;
        GridToggle.IsChecked = on;
        if (on)
        {
            if (_cropMode) SetCropMode(false);
            if (_zoomed) SetZoom(false, null);
        }
        PhotoBorder.IsVisible = !on;
        Grid.SetRow(LibraryPanel, on ? 0 : 1);
        Grid.SetRowSpan(LibraryPanel, on ? 2 : 1);
        LibraryList.Classes.Set("strip", !on);
        LibraryList.Classes.Set("grid", on);
        LibraryList.ItemsPanel = on
            ? new FuncTemplate<Panel?>(() => new WrapPanel())
            : new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { Orientation = Orientation.Horizontal });
        ScrollViewer.SetHorizontalScrollBarVisibility(LibraryList, on ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(LibraryList, on ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);

        // Повернулись із сітки, вибравши там одне інше фото, — відкриваємо його.
        if (!on && LibraryList.SelectedItems?.Count == 1 && LibraryList.SelectedItem is PhotoItem item && item != _currentItem)
            _ = OpenAsync(item.Path);
        else if (_currentItem is not null)
            Dispatcher.UIThread.Post(() => LibraryList.ScrollIntoView(_currentItem));
    }

    /// <summary>У стрічці коліщатко миші гортає вбік.</summary>
    private void OnStripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_gridMode || LibraryList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } viewer) return;
        viewer.Offset = viewer.Offset.WithX(viewer.Offset.X - (e.Delta.Y + e.Delta.X) * 120);
        e.Handled = true;
    }

    private void ApplyFilter()
    {
        var match = Filters[Math.Max(0, FilterBox.SelectedIndex)].Match;
        _syncingSelection = true;
        _items.Clear();
        foreach (var item in _allItems.Where(match)) _items.Add(item);
        _syncingSelection = false;
        if (_currentItem is not null && _items.Contains(_currentItem)) MarkCurrent(_currentItem);
        UpdateLibraryText();
    }

    private void UpdateLibraryText()
    {
        if (_folder is null) return;
        int selected = LibraryList.SelectedItems?.Count ?? 0;
        string count = _items.Count == _allItems.Count ? $"{_allItems.Count} фото" : $"{_items.Count} з {_allItems.Count} фото";
        LibraryText.Text = $"📁 {Path.GetFileName(_folder)} · {count}" + (selected > 1 ? $" · вибрано {selected}" : "");
    }

    // ── Рейтинги й позначки ────────────────────────────────────────────────────

    private void SetRating(int rating)
    {
        foreach (var item in SelectedOrCurrent())
        {
            item.Rating = rating;
            _catalog.Set(item.Path, item.Rating, item.Flag);
        }
        ApplyFilterKeepingView();
    }

    private void SetFlag(PhotoFlag flag)
    {
        foreach (var item in SelectedOrCurrent())
        {
            item.Flag = flag;
            _catalog.Set(item.Path, item.Rating, item.Flag);
        }
        ApplyFilterKeepingView();
    }

    /// <summary>Після зміни рейтингу фільтр перераховується, лише якщо він увімкнений (щоб стрічка не «стрибала» дарма).</summary>
    private void ApplyFilterKeepingView()
    {
        if (FilterBox.SelectedIndex > 0) ApplyFilter();
    }

    // ── Відібрані ──────────────────────────────────────────────────────────────

    private async void OnMovePicksClick(object? sender, RoutedEventArgs e) => await MovePicksAsync();

    /// <summary>
    /// «📂 У «Відібрані»»: фото з позначкою ⚑ фізично переїжджають у підпапку «Відібрані» — разом із правками,
    /// рейтингом і варіантами AI. Зі стрічки вони зникають; якщо в папці нічого не лишилось — відкривається «Відібрані».
    /// </summary>
    private async Task MovePicksAsync()
    {
        if (_folder is not { } folder) return;
        if (string.Equals(Path.GetFileName(folder), PhotoMover.PicksFolder, StringComparison.OrdinalIgnoreCase))
        {
            ActionText.Text = $"📂 Це вже папка «{PhotoMover.PicksFolder}»";
            return;
        }
        var picks = _allItems.Where(i => i.Flag == PhotoFlag.Pick).ToList();
        if (picks.Count == 0)
        {
            ActionText.Text = "⚑ Спершу познач фото клавішею P — тоді їх можна перемістити в «Відібрані»";
            return;
        }
        string target = Path.Combine(folder, PhotoMover.PicksFolder);
        if (!await Notice.ConfirmAsync(this, $"Перемістити {picks.Count} фото в «{PhotoMover.PicksFolder}»?",
                $"Файли переїдуть у {target} разом із правками, рейтингом і варіантами AI. Зі стрічки цієї папки вони зникнуть.",
                "📂 Перемістити"))
            return;

        CommitEdits();
        int currentIndex = _currentItem is null ? -1 : _items.IndexOf(_currentItem);
        bool currentMoves = _currentItem is not null && picks.Contains(_currentItem);
        if (currentMoves)
        {
            // Відпускаємо поточне фото: правки вже записані, а фонове розкодування не повинно тримати файл.
            _openGeneration++;
            _photoPath = null;
            if (_fullTask is { } full) try { await full; } catch { }
        }
        foreach (var item in picks)
            if (_prefetch.Remove(item.Path, out var ready)) try { await ready; } catch { }

        var moved = new List<PhotoItem>();
        var failed = new List<string>();
        foreach (var item in picks)
        {
            ActionText.Text = $"📂 Переміщую {moved.Count + 1} з {picks.Count}: {item.Name}…";
            try
            {
                string newPath = await Task.Run(() => PhotoMover.Move(item.Path, target));
                _catalog.Rename(item.Path, newPath);
                AiStyleDialog.MoveHistory(item.Path, newPath);
                moved.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{item.Name}: {ex.Message}");
            }
        }

        _allItems = _allItems.Except(moved).ToList();
        bool currentGone = _currentItem is not null && moved.Contains(_currentItem);
        if (currentGone) _currentItem = null;
        ApplyFilter();

        if (_allItems.Count == 0)
        {
            await OpenFolderAsync(target, null);
        }
        else if (currentMoves)
        {
            // Поточне не вдалось перемістити — відкриваємо його знову; інакше — сусіднє (або будь-яке, якщо фільтр тепер порожній).
            var next = !currentGone ? _currentItem!
                : _items.Count > 0 ? _items[Math.Clamp(currentIndex, 0, _items.Count - 1)] : _allItems[0];
            await OpenAsync(next.Path);
        }

        ActionText.Text = $"📂 У «{PhotoMover.PicksFolder}» переміщено {moved.Count}" + (failed.Count > 0 ? $" з {picks.Count}" : "") + " фото";
        if (failed.Count > 0)
            await Notice.ShowAsync(this, $"⚠️ Не переміщено {failed.Count} фото", string.Join("\n", failed));
    }

    // ── Пакетні дії ────────────────────────────────────────────────────────────

    private void OnCopySettingsClick(object? sender, RoutedEventArgs e) => CopySettings();
    private void OnPasteSettingsClick(object? sender, RoutedEventArgs e) => PasteSettings();

    private async void CopySettings()
    {
        if (_photoPath is not { } path) return;
        CommitEdits();
        var remembered = _ui.CopyGroups
            ?? StylePresets.SettingGroups.Select(g => g.Name).Where(g => g != StylePresets.GeometryGroup && g != StylePresets.MasksGroup && g != StylePresets.HealGroup
                && g != StylePresets.PerspectiveGroup).ToHashSet();
        var groups = await new CopyDialog(remembered).ShowDialog<HashSet<string>?>(this);
        if (groups is null) return;
        _ui.CopyGroups = groups;
        _ui.Save();

        _copiedSettings = StylePresets.FromSettings("Скопійовані правки", _settings, groups);
        PasteButton.IsEnabled = true;
        string what = groups.Count == StylePresets.SettingGroups.Length ? "усі правки"
            : groups.Count == 1 ? $"«{groups.First()}»" : $"{groups.Count} груп правок";
        ActionText.Text = $"📋 Скопійовано {what} з {Path.GetFileName(path)}";
    }

    private void PasteSettings()
    {
        if (_copiedSettings is not { } style) return;
        var targets = SelectedOrCurrent();
        ApplyToPhotos(targets, (_, s) => StylePresets.Apply(s, style));
        ActionText.Text = $"📌 Правки вставлено: {targets.Count} фото";
    }

    private async void OnAiSeriesClick(object? sender, RoutedEventArgs e) => await OpenAiSeriesAsync();

    /// <summary>«✨ AI для серії»: спільний стиль від Gemini для вибраних фото (у порядку стрічки).</summary>
    private async Task OpenAiSeriesAsync()
    {
        var selected = SelectedOrCurrent().ToHashSet();
        var targets = _items.Where(selected.Contains).ToList();
        if (targets.Count < 2)
        {
            ActionText.Text = "✨ Вибери в стрічці чи сітці кілька фото серії (Ctrl+клік або Shift+клік)";
            return;
        }
        CommitEdits();
        var context = new SeriesStyleContext(targets.Select(t => t.Path).ToList(),
            path => string.Equals(path, _photoPath, StringComparison.OrdinalIgnoreCase) ? _settings.Copy() : EditSidecar.Load(path),
            _ui, (looks, name) =>
            {
                var byPath = looks.ToDictionary(l => l.Path, l => l.Settings, StringComparer.OrdinalIgnoreCase);
                var items = _allItems.Where(i => byPath.ContainsKey(i.Path)).ToList();
                if (_photoPath is not null && byPath.ContainsKey(_photoPath))
                {
                    if (_healMode) SetHealMode(false);
                    _maskIndex = -1;
                }
                ApplyToPhotos(items, (path, _) => byPath[path]);
                ActionText.Text = $"✨ Стиль «{name}» від Gemini застосовано до {looks.Count} фото (на поточному — Ctrl+Z, щоб скасувати)";
            });
        await new SeriesStyleDialog(context).ShowDialog(this);
    }

    /// <summary>
    /// Змінює правки кількох фото: поточного — як звичайну дію (з Ctrl+Z), решти — прямо у їхніх файлах правок;
    /// їхні мініатюри перераховуються у фоні.
    /// </summary>
    /// <param name="change">Шлях фото і його поточні правки → нові правки.</param>
    private void ApplyToPhotos(IReadOnlyList<PhotoItem> targets, Func<string, DevelopSettings, DevelopSettings> change)
    {
        foreach (var item in targets)
        {
            if (string.Equals(item.Path, _photoPath, StringComparison.OrdinalIgnoreCase))
            {
                CommitEdits();
                ApplySettings(change(item.Path, _settings));
                OnSettingsEdited();
                continue;
            }
            try
            {
                var updated = change(item.Path, EditSidecar.Load(item.Path));
                EditSidecar.Save(item.Path, updated);
                item.Edited = !updated.IsDefaultFor(item.Path);
                _editedThumbs.Writer.TryWrite(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ActionText.Text = $"⚠️ {item.Name}: {ex.Message}";
            }
        }
    }

    /// <summary>Експорт кількох фото в одну папку — по черзі, кожне в повній роздільності й найвищій якості.</summary>
    private async Task ExportManyAsync(IReadOnlyList<PhotoItem> items)
    {
        string? target = ExportFolder();
        if (target is null)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Куди експортувати {items.Count} фото",
                SuggestedStartLocation = _folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(_folder),
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } picked) return;
            target = picked;
        }

        CommitEdits();
        ExportButton.IsEnabled = false;
        _exportsRunning++; // і між фото вікно не закриється
        int done = 0;
        try
        {
            foreach (var item in items)
            {
                var settings = string.Equals(item.Path, _photoPath, StringComparison.OrdinalIgnoreCase) ? _settings.Copy() : EditSidecar.Load(item.Path);
                string path = ResultPath(target, item.Path);
                ActionText.Text = $"Експорт {done + 1} з {items.Count}: {Path.GetFileName(path)}…";
                if (await ExportOneAsync(item.Path, settings, path)) done++;
            }
            ActionText.Text = $"✅ Експортовано {done} з {items.Count} у «{Path.GetFileName(target)}»";
        }
        finally
        {
            _exportsRunning--;
            ExportButton.IsEnabled = _photoPath is not null;
        }
    }
}
