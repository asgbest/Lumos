using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lumos.Core;
using Thumb = Lumos.Core.Thumb;

namespace Lumos.App;

/// <summary>
/// Що потрібно вікну «✨ AI для серії» від головного: фото серії (у порядку стрічки), їхні поточні правки
/// і як накласти готовий стиль на всі разом.
/// </summary>
public sealed record SeriesStyleContext(IReadOnlyList<string> Paths, Func<string, DevelopSettings> CurrentFor, UiState Ui,
    Action<IReadOnlyList<(string Path, DevelopSettings Settings)>, string> Apply);

/// <summary>
/// «✨ AI для серії»: Gemini бачить усі вибрані кадри разом і пропонує кілька спільних стилів — один вигляд на серію
/// плюс поправки окремим кадрам, щоб вони стали однорідними. Кожен варіант показано на всіх кадрах; «Застосувати» — на всі.
/// </summary>
public sealed class SeriesStyleDialog : Window
{
    private const string KeyName = "gemini-api-key";
    private const int Looks = 3;
    private const int CardThumb = 150;

    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D8D8D8"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#909090"));

    private readonly SeriesStyleContext _context;
    private readonly TextBox _wish = new()
    {
        Watermark = "Побажання (необов'язково): «тепла осінь», «холодний туманний ранок», «як на плівку»…",
        AcceptsReturn = false, FontSize = 12,
    };
    private readonly Button _suggest = new() { Content = "✨ Запропонувати", Classes = { "accent" }, IsDefault = true, IsEnabled = false };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _results = new() { Spacing = 10 };
    private readonly CheckBox _review = new()
    {
        Content = "Перевіряти результат", IsChecked = true, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        [ToolTip.TipProperty] = "Gemini бачить усі кадри з кожним стилем і вирівнює їх — помітно краще, але вдвічі довше",
    };
    private readonly List<SeriesPhoto> _photos = [];
    private CancellationTokenSource? _cancel;

    public SeriesStyleDialog(SeriesStyleContext context)
    {
        _context = context;
        Title = $"✨ AI для серії · {context.Paths.Count} фото";
        Width = 900;
        Height = 860;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));
        _wish.Text = context.Ui.AiWish;

        _suggest.Click += async (_, _) => await SuggestAsync();
        Closing += (_, _) =>
        {
            _cancel?.Cancel();
            _context.Ui.AiWish = _wish.Text;
            _context.Ui.Save();
        };

        int shown = AiSeries.Sample(context.Paths.Count).Length;
        string about = $"Gemini побачить {(shown < context.Paths.Count ? $"{shown} кадрів із {context.Paths.Count} (рівномірно через серію)" : "усі кадри")} " +
                       $"({AiSeries.ImageSide} px) і запропонує спільний стиль та поправки окремим кадрам (експозиція, баланс білого, тон), " +
                       "щоб серія стала однорідною. Обрізка, вирівнювання й прибрані плями кожного фото лишаються. " +
                       "З перевіркою відповідь іде ~2–3 хв.";
        Content = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Top, Spacing = 8,
                    Children =
                    {
                        _wish,
                        new DockPanel
                        {
                            Children =
                            {
                                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Left,
                                    Margin = new Thickness(0, 0, 12, 0), Children = { _suggest, _review } },
                                _status,
                            },
                        },
                        new TextBlock { Text = about, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim },
                    },
                },
                new ScrollViewer { Margin = new Thickness(0, 12, 0, 0), Content = _results, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            },
        };

        Opened += async (_, _) => await LoadPhotosAsync();
    }

    /// <summary>Прев'ю кадрів (зменшені до 1024 — для Gemini, ШІ-масок і мініатюр досить), по три одночасно.</summary>
    private async Task LoadPhotosAsync()
    {
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        var paths = _context.Paths;
        var currents = paths.Select(_context.CurrentFor).ToArray();
        var loaded = new SeriesPhoto?[paths.Count];
        int done = 0;
        _status.Text = $"Готую кадри: 0 з {paths.Count}…";
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count), new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancel },
                (i, token) =>
                {
                    try
                    {
                        var photo = PhotoDecoder.Load(paths[i], forPreview: true);
                        photo = photo with { Image = photo.Image.Downscale(1024) };
                        loaded[i] = new SeriesPhoto(paths[i], photo, currents[i]);
                    }
                    catch (Exception ex) when (ex is IOException or Sdcb.LibRaw.LibRawException or ImageMagick.MagickException) { }
                    int n = Interlocked.Increment(ref done);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _status.Text = $"Готую кадри: {n} з {paths.Count}…");
                    return ValueTask.CompletedTask;
                });
        }
        catch (OperationCanceledException) { return; }
        _photos.AddRange(loaded.OfType<SeriesPhoto>());
        int failed = paths.Count - _photos.Count;
        _suggest.IsEnabled = _photos.Count >= 2;
        _status.Text = _photos.Count < 2 ? "⚠️ Не вдалося відкрити кадри серії."
            : $"Кадрів: {_photos.Count}" + (failed > 0 ? $" (не відкрилось: {failed})" : "") + ". «Запропонувати» — і Gemini подивиться на серію.";
    }

    private async Task SuggestAsync()
    {
        string apiKey = AiStylist.ApiKey(SecretStore.Load(KeyName));
        string model = _context.Ui.GeminiModel ?? AiStylist.DefaultModel;
        _cancel?.Cancel();
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        _suggest.IsEnabled = false;
        string? wish = _wish.Text;
        var photos = _photos.ToList();
        var sample = AiSeries.Sample(photos.Count);
        var shown = sample.Select(i => photos[i]).ToList();
        try
        {
            _status.Text = $"👀 Gemini дивиться на {shown.Count} кадрів…";
            var aiKinds = AiMaskTypes.All.Where(t => AiModels.IsInstalled(t.Model)).Select(t => t.Kind).ToList();
            string system = AiSeries.SystemInstruction(aiKinds, Looks, shown.Count);
            // Яскравість кадрів вирівнюємо самі — Gemini бачить уже вирівняні кадри й доправляє решту.
            var baseline = await Task.Run(() => AiSeries.ExposureBaseline(photos), cancel);
            var asShot = await Task.Run(() => shown.Select((p, k) =>
            {
                double ev = baseline[sample[k]];
                var (bgra, w, h) = AiLooks.RenderAsShot(p.Path, p.Photo.Image, p.Current, p.Photo.Profile, withHeals: true, ev);
                return (Photo: p, Baseline: ev, Jpeg: AiLooks.ToJpeg(bgra, w, h, AiSeries.ImageSide), Thumb: Thumbnails.Resize(new Thumb(w, h, bgra), AiSeries.SheetCell));
            }).ToList(), cancel);
            string answer = await AiStylist.AskGeminiAsync(apiKey, model, system,
                AiSeries.Parts(asShot.Select(a => (a.Photo, a.Jpeg, a.Baseline)).ToList(), wish), cancel);
            var looks = AiSeries.Parse(answer, shown.Count);

            if (_review.IsChecked == true)
            {
                _status.Text = $"🎨 Рендерю {looks.Count} стилі на всіх кадрах і показую Gemini для перевірки…";
                int columns = AiSeries.Columns(shown.Count);
                byte[] original = AiSeries.Sheet(asShot.Select(a => a.Thumb).ToList(), columns);
                var sheets = await Task.Run(() => looks.Select(look =>
                {
                    var thumbs = shown.Select((p, k) =>
                    {
                        var settings = AiLooks.Materialize(AiSeries.ForPhoto(look, k, p, baseline[sample[k]]), p.Path, p.Photo.Image, p.Photo.Profile, []);
                        return AiLooks.Render(p.Photo, settings, AiSeries.SheetCell, crop: false);
                    }).ToList();
                    return (look, AiSeries.Sheet(thumbs, columns));
                }).ToList(), cancel);
                _status.Text = "🧐 Gemini перевіряє серію й вирівнює кадри…";
                string refined = await AiStylist.AskGeminiAsync(apiKey, model, system, AiSeries.RefineParts(original, sheets, shown.Count, wish), cancel);
                looks = AiSeries.Parse(refined, shown.Count);
            }

            for (int i = 0; i < looks.Count; i++)
            {
                _status.Text = $"Готую прев'ю: стиль {i + 1} з {looks.Count}…";
                await AddCardAsync(looks[i], photos, sample, baseline, i, cancel);
            }
            _status.Text = $"✨ Стилів: {looks.Count} (від {model}). «Застосувати» — і стиль на всіх {photos.Count} фото.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            _status.Text = $"⚠️ {ex.Message}";
        }
        finally
        {
            _suggest.IsEnabled = _photos.Count >= 2;
        }
    }

    /// <summary>Картка стилю: опис і всі кадри з ним; правки кожного кадру (з ШІ-масками) рахуються тут же, по черзі.</summary>
    private async Task AddCardAsync(SeriesLook look, List<SeriesPhoto> photos, int[] sample, double[] baseline, int position, CancellationToken cancel)
    {
        var strip = new WrapPanel();
        var apply = new Button { Content = $"Застосувати до {photos.Count} фото", Classes = { "accent" }, FontSize = 12, IsEnabled = false };
        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new TextBlock { Text = look.Name, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = look.Description, FontSize = 12, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        if (look.Critique is not null)
            info.Children.Add(new TextBlock { Text = "🧐 Після перевірки: " + look.Critique, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        int corrected = look.Offsets.Count;
        info.Children.Add(new TextBlock
        {
            Text = corrected == 0 ? "Поправок окремим кадрам немає" : $"⚖️ Поправки окремим кадрам: {corrected}",
            FontSize = 11, Foreground = Dim,
        });
        var skippedText = new TextBlock { FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        info.Children.Add(skippedText);
        info.Children.Add(strip);
        info.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { apply } });
        _results.Children.Insert(Math.Min(position, _results.Children.Count),
            new Border { Background = new SolidColorBrush(Color.Parse("#2E2E2E")), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = info });

        var results = new List<(string Path, DevelopSettings Settings)>();
        var skipped = new HashSet<string>();
        foreach (var (photo, index) in photos.Select((p, i) => (p, i)))
        {
            var (settings, thumb) = await Task.Run(() =>
            {
                var missing = new List<string>();
                var s = AiLooks.Materialize(AiSeries.ForPhoto(look, AiSeries.Nearest(sample, index), photo, baseline[index]), photo.Path, photo.Photo.Image, photo.Photo.Profile, missing);
                lock (skipped) skipped.UnionWith(missing);
                return (s, AiLooks.Render(photo.Photo, s, CardThumb * 2, crop: true));
            }, cancel);
            results.Add((photo.Path, settings));
            var image = new Image
            {
                Source = MainWindow.ToBitmap(thumb), Stretch = Stretch.Uniform, Width = CardThumb, Height = CardThumb,
                Margin = new Thickness(0, 4, 6, 0),
            };
            ToolTip.SetTip(image, System.IO.Path.GetFileName(photo.Path));
            strip.Children.Add(image);
        }
        if (skipped.Count > 0)
        {
            skippedText.Text = "Пропущено (немає моделі ШІ): " + string.Join(", ", skipped);
            skippedText.IsVisible = true;
        }

        apply.IsEnabled = true;
        apply.Click += (_, _) =>
        {
            // Геометрія, обрізка, латки й розмиття — з кожного фото як воно є зараз (могло змінитись, поки вікно відкрите).
            var final = results.Select(r =>
            {
                var current = _context.CurrentFor(r.Path);
                var applied = r.Settings.Copy();
                applied.CopyGeometryFrom(current);
                (applied.CropX, applied.CropY, applied.CropW, applied.CropH) = (current.CropX, current.CropY, current.CropW, current.CropH);
                applied.Heals = current.Heals;
                (applied.BlurAmount, applied.BlurRange, applied.BlurBokeh) = (current.BlurAmount, current.BlurRange, current.BlurBokeh);
                (applied.BlurFocusX, applied.BlurFocusY, applied.DepthMap) = (current.BlurFocusX, current.BlurFocusY, current.DepthMap);
                return (r.Path, applied);
            }).ToList();
            _context.Apply(final, look.Name);
            _status.Text = $"🎨 «{look.Name}» на {final.Count} фото. Можна спробувати інший або закрити вікно.";
        };
    }
}
