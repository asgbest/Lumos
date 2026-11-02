using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lumos.Core;
using Thumb = Lumos.Core.Thumb;

namespace Lumos.App;

/// <summary>
/// Що потрібно панелі «✨ AI» від головного вікна: фото, його прев'ю, поточні правки (на момент запиту — панель живе довго,
/// а обрізку тим часом можуть змінити) і як накласти варіант.
/// </summary>
public sealed record AiStyleContext(Photo Photo, string Path, LinearImage Preview, ToneProfile Profile, Func<DevelopSettings> GetCurrent,
    UiState Ui, Action<DevelopSettings, string> Apply)
{
    public DevelopSettings Current => GetCurrent();
}

/// <summary>
/// «✨ AI-стиліст» — панель праворуч від фото: зменшена копія фото й опис можливостей Lumos ідуть у Gemini, той пропонує
/// кілька стилістик одразу числами повзунків (і маски, де треба), а програма показує кожен варіант прев'ю — клік накладає його на фото.
/// Ключ API вводиться тут і зберігається зашифрованим лише на цьому комп'ютері.
/// </summary>
public sealed class AiStylePanel : UserControl
{
    private const string KeyName = "gemini-api-key";
    private const int Looks = 4;

    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D8D8D8"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#909090"));

    private readonly AiStyleContext _context;
    private readonly TextBox _key = new() { PasswordChar = '•', Watermark = "Ключ Gemini API", FontSize = 12 };
    private readonly TextBox _model = new() { FontSize = 12, Watermark = AiStylist.DefaultModel };
    private readonly StackPanel _keyPanel = new() { Spacing = 6 };
    private readonly TextBox _wish = new()
    {
        Watermark = "Побажання (необов'язково): «тепло, як на плівку», «драматичне небо», «чорно-біле»…",
        AcceptsReturn = false, FontSize = 12,
    };
    private readonly Button _suggest = new() { Content = "✨ Запропонувати", Classes = { "accent" }, IsDefault = true };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _results = new() { Spacing = 10 };
    private readonly AiWaiting _waiting = new();
    private readonly CheckBox _review = new()
    {
        Content = "Перевіряти результат", IsChecked = true, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        [ToolTip.TipProperty] = "Gemini бачить, що реально вийшло, і виправляє числа — помітно краще, але вдвічі довше",
    };
    private readonly CheckBox _pro = new()
    {
        Content = "🎓 Pro", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        [ToolTip.TipProperty] = "Як у професіонала: Lumos сам знаходить небо, гори, воду, об'єкт і міряє їх, Gemini складає план обробки " +
                                "(4–8 локальних кроків — кожен своєю маскою) і що прибрати, а перевірка дивиться ще й на кожну ділянку. ~3 хв",
    };
    private CancellationTokenSource? _cancel;

    /// <summary>Готовий варіант: те, що показує картка (без мініатюри — її легко перерахувати).</summary>
    /// <param name="Steps">«✨ AI Pro»: кроки плану; Removed і Removals — що прибрано ШІ-видаленням (назви й латки).</param>
    public sealed record Look(string Name, string Description, string? Critique, DevelopSettings Settings, List<string> Skipped,
        List<string>? Steps = null, List<string>? Removed = null, EquatableList<HealSpot>? Removals = null);

    /// <summary>
    /// Варіанти кожного фото зберігаються на диску (%LocalAppData%\Lumos\AiLooks): вікно можна закрити, програму перезапустити —
    /// вони на місці; нові додаються зверху.
    /// </summary>
    private static readonly Dictionary<string, List<Look>> History = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Look, Thumb> Thumbs = new(ReferenceEqualityComparer.Instance);
    private const int MaxKept = 16;
    private const int MaxReviews = 2; // кіл перевірки: друге — лише якщо вимірювання ще бачать огріхи

    private static string HistoryFile(string photoPath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(photoPath).ToLowerInvariant())));
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumos", "AiLooks", hash + ".json");
    }

    private static List<Look> LoadHistory(string photoPath)
    {
        if (History.TryGetValue(photoPath, out var kept)) return kept;
        kept = [];
        try
        {
            string file = HistoryFile(photoPath);
            if (File.Exists(file)) kept = System.Text.Json.JsonSerializer.Deserialize<List<Look>>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return History[photoPath] = kept;
    }

    private static void SaveHistory(string photoPath, List<Look> kept)
    {
        try
        {
            string file = HistoryFile(photoPath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(kept));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Фото переїхало в іншу папку — його варіанти AI теж (вони прив'язані до шляху).</summary>
    internal static void MoveHistory(string oldPath, string newPath)
    {
        if (History.Remove(oldPath, out var kept)) History[newPath] = kept;
        try
        {
            string from = HistoryFile(oldPath), to = HistoryFile(newPath);
            if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Натиснули «✕» — головне вікно ховає панель (запит, якщо йде, доходить до кінця й лишається в історії).</summary>
    public event Action? CloseRequested;

    /// <summary>Для якого фото панель.</summary>
    public string PhotoPath => _context.Path;

    /// <summary>Gemini ще думає — панель не замінюють панеллю іншого фото, щоб не обірвати запит.</summary>
    public bool Busy => !_suggest.IsEnabled;

    public AiStylePanel(AiStyleContext context)
    {
        _context = context;

        _model.Text = context.Ui.GeminiModel ?? AiStylist.DefaultModel;
        _wish.Text = context.Ui.AiWish;
        _pro.IsChecked = context.Ui.AiPro;

        var saveKey = new Button { Content = "Зберегти ключ", FontSize = 12 };
        saveKey.Click += (_, _) => SaveKey();
        var forget = new Button { Content = "Забути ключ", FontSize = 12 };
        forget.Click += (_, _) =>
        {
            SecretStore.Save(KeyName, null);
            _key.Text = "";
            _status.Text = "Свій ключ видалено — працює вбудований.";
        };
        _keyPanel.Children.Add(new TextBlock
        {
            Text = "Програма вже має вбудований ключ Gemini API. Сюди можна вставити свій (створюється на aistudio.google.com/apikey) — " +
                   "він зберігається зашифрованим лише на цьому комп'ютері й замінює вбудований; «Забути ключ» — повернутися до вбудованого.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Text,
        });
        _keyPanel.Children.Add(_key);
        _keyPanel.Children.Add(new DockPanel
        {
            Children =
            {
                new TextBlock { Text = "Модель:", FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, [DockPanel.DockProperty] = Dock.Right, Margin = new Thickness(8, 0, 0, 0),
                    Children = { forget, saveKey } },
                _model,
            },
        });
        _keyPanel.IsVisible = false; // ключ вбудований — панель лише для заміни на свій

        var keyToggle = new Button { Content = "🔑 Ключ", FontSize = 12 };
        keyToggle.Click += (_, _) => _keyPanel.IsVisible = !_keyPanel.IsVisible;
        _suggest.Click += async (_, _) => await SuggestAsync();
        var close = new Button { Content = "✕", FontSize = 12, Padding = new Thickness(8, 2), [ToolTip.TipProperty] = "Сховати панель (клавіша A)" };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var clear = new Button
        {
            Content = "🗑 Очистити", FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 0),
            [ToolTip.TipProperty] = "Видалити всі варіанти Gemini для цього фото (застосовані правки лишаються)",
        };
        clear.Click += async (_, _) => await ClearAsync();

        Content = new DockPanel
        {
            Margin = new Thickness(14, 10, 14, 0),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Top, Spacing = 8,
                    Children =
                    {
                        new DockPanel
                        {
                            Children =
                            {
                                new StackPanel { [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Children = { clear, close } },
                                new TextBlock
                                {
                                    Text = "✨ AI-стиліст · " + System.IO.Path.GetFileName(context.Path), FontSize = 14, FontWeight = FontWeight.SemiBold,
                                    Foreground = Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                },
                            },
                        },
                        _keyPanel,
                        _wish,
                        new WrapPanel { Orientation = Orientation.Horizontal, Children = { _suggest, keyToggle, _review, _pro },
                            ItemSpacing = 8, LineSpacing = 6 },
                        _status,
                        new TextBlock
                        {
                            Text = $"Gemini отримає копію фото ({AiStylist.ImageSide} px), а для перевірки — рендери варіантів з вимірюваннями; " +
                                   "~1–2 хв, у 🎓 Pro — ~3 хв. «Застосувати» замінює поточні правки (обрізка й прибрані плями лишаються); Ctrl+Z — назад.",
                            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim,
                        },
                    },
                },
                new Grid
                {
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { new ScrollViewer { Content = _results, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, _waiting },
                },
            },
        };

        _ = ShowKeptAsync();
    }

    /// <summary>Побажання, модель і «Pro» — у налаштування (панель ховають чи замінюють панеллю іншого фото).</summary>
    public void SaveUi()
    {
        _context.Ui.AiWish = _wish.Text;
        _context.Ui.AiPro = _pro.IsChecked == true;
        _context.Ui.GeminiModel = string.IsNullOrWhiteSpace(_model.Text) || _model.Text.Trim() == AiStylist.DefaultModel ? null : _model.Text.Trim();
        _context.Ui.Save();
    }

    /// <summary>«🗑 Очистити»: усі варіанти Gemini цього фото геть — і зі списку, і з диска. Правки фото не змінюються.</summary>
    private async Task ClearAsync()
    {
        string path = _context.Path;
        var kept = LoadHistory(path);
        if (kept.Count == 0)
        {
            _status.Text = "Варіантів Gemini для цього фото немає.";
            return;
        }
        if (TopLevel.GetTopLevel(this) is Window owner && !await Notice.ConfirmAsync(owner,
                $"Видалити {kept.Count} варіантів Gemini для цього фото?",
                "Список очиститься. Те, що вже застосовано до фото, лишається (Ctrl+Z працює як завжди).", "🗑 Видалити"))
            return;
        foreach (var look in kept) Thumbs.Remove(look);
        kept.Clear();
        _results.Children.Clear();
        try { File.Delete(HistoryFile(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _status.Text = "🗑 Варіанти Gemini для цього фото видалено.";
    }

    /// <summary>Обірвати запит (програму закривають).</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>Попередні варіанти цього фото — одразу при відкритті вікна.</summary>
    private async Task ShowKeptAsync()
    {
        var c = _context;
        var kept = LoadHistory(c.Path);
        if (kept.Count == 0) return;
        _status.Text = $"Попередні варіанти для цього фото: {kept.Count}. «Запропонувати» — додати нові зверху.";
        foreach (var look in kept.ToList())
        {
            if (!Thumbs.TryGetValue(look, out var thumb))
                Thumbs[look] = thumb = await Task.Run(() => AiLooks.Render(c.Photo, look.Settings, 300, crop: true));
            _results.Children.Add(Card(look, thumb));
        }
    }

    private bool SaveKey()
    {
        string key = _key.Text?.Trim() ?? "";
        if (key.Length == 0) return SecretStore.Load(KeyName) is { Length: > 0 };
        try
        {
            SecretStore.Save(KeyName, key);
            _key.Text = "";
            _keyPanel.IsVisible = false;
            _status.Text = "🔑 Ключ збережено.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = $"⚠️ Не вдалося зберегти ключ: {ex.Message}";
            return false;
        }
    }

    private async Task SuggestAsync()
    {
        SaveKey();
        string apiKey = AiStylist.ApiKey(SecretStore.Load(KeyName));
        string model = string.IsNullOrWhiteSpace(_model.Text) ? AiStylist.DefaultModel : _model.Text.Trim();
        _cancel?.Cancel();
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        _suggest.IsEnabled = false;
        _status.Text = "👀 Gemini дивиться на фото…";
        _waiting.Start();
        var c = _context;
        string? wish = _wish.Text;
        try
        {
            if (_pro.IsChecked == true)
            {
                var pro = await AiPro.RunAsync(apiKey, model, c.Photo, c.Path, c.Preview, c.Profile, c.Current, wish, Looks,
                    _review.IsChecked == true, s => _status.Text = s, cancel);
                var removals = new EquatableList<HealSpot>(pro.Removed.Select(r => r.Spot));
                await ShowAsync(pro.Looks, model, s => s with
                {
                    Steps = pro.Plan.Steps.ToList(), Removed = pro.Removed.Select(r => r.Name).ToList(), Removals = removals,
                }, cancel);
                return;
            }

            // ШІ-маски пропонуємо лише ті, чиї моделі вже є на комп'ютері (без завантажень посеред відповіді).
            var aiKinds = AiMaskTypes.All.Where(t => AiModels.IsInstalled(t.Model)).Select(t => t.Kind).ToList();
            string system = AiStylist.SystemInstruction(aiKinds, Looks);
            var (original, originalMetrics) = await Task.Run(() =>
            {
                var (bgra, w, h) = AiLooks.RenderAsShot(c.Path, c.Preview, c.Current, c.Profile, withHeals: true);
                return (AiLooks.ToJpeg(bgra, w, h, AiStylist.ImageSide), LookMetrics.Measure(bgra, w, h));
            }, cancel);
            string answer = await AiStylist.AskGeminiAsync(apiKey, model, system, AiStylist.UserPrompt(c.Photo, c.Current, wish), original, cancel);
            var suggestions = AiStylist.Parse(answer, c.Current, c.Path);

            if (_review.IsChecked == true)
            {
                // Перевірка: модель бачить, що реально вийшло в нашому рушії, і вимірювання кожного рендера (темні третини,
                // провали, відтінок сірого) — і виправляє числа. Якщо після першого виправлення огріхи лишились — ще одне коло.
                for (int round = 1; round <= MaxReviews; round++)
                {
                    _status.Text = round == 1
                        ? $"🎨 Рендерю {suggestions.Count} варіанти й показую їх Gemini для перевірки…"
                        : "📏 Перевіряю виправлені варіанти…";
                    var current = suggestions;
                    var renders = await Task.Run(() => current.Select(s =>
                    {
                        var settings = AiLooks.Materialize(s, c.Path, c.Preview, c.Profile, []);
                        var full = AiLooks.Render(c.Photo, settings, AiStylist.RenderSide, crop: false);
                        return (s, AiLooks.ToJpeg(full.Bgra, full.Width, full.Height, AiStylist.RenderSide),
                            LookMetrics.Measure(full.Bgra, full.Width, full.Height));
                    }).ToList(), cancel);
                    int flawed = renders.Count(r => r.Item3.Problems(originalMetrics).Count > 0);
                    if (round > 1 && flawed == 0) break;
                    _status.Text = round == 1
                        ? "🧐 Gemini перевіряє результат і виправляє…"
                        : $"🧐 У {flawed} з {renders.Count} варіантів ще є огріхи — Gemini виправляє вдруге…";
                    string refined = await AiStylist.AskGeminiAsync(apiKey, model, system,
                        AiStylist.RefineParts(original, originalMetrics, renders, wish), cancel);
                    suggestions = AiStylist.Parse(refined, c.Current, c.Path);
                }
            }

            await ShowAsync(suggestions, model, l => l, cancel);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            _status.Text = $"⚠️ {ex.Message}";
        }
        finally
        {
            _waiting.Stop();
            _suggest.IsEnabled = true;
        }
    }

    /// <summary>Нові варіанти — прев'ю й картки зверху (попередні лишаються нижче), і в історію фото.</summary>
    /// <param name="extra">Що додати до картки (для Pro — план і прибране).</param>
    private async Task ShowAsync(List<AiSuggestion> suggestions, string model, Func<Look, Look> extra, CancellationToken cancel)
    {
        var c = _context;
        _waiting.Stop();
        _status.Text = $"Готую прев'ю {suggestions.Count} варіантів…";
        var kept = LoadHistory(c.Path);
        for (int i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            var (settings, thumb, skipped) = await Task.Run(() => Build(suggestion), cancel);
            var look = extra(new Look(suggestion.Name, suggestion.Description, suggestion.Critique, settings, skipped));
            Thumbs[look] = thumb;
            kept.Insert(i, look);
            _results.Children.Insert(Math.Min(i, _results.Children.Count), Card(look, thumb));
        }
        while (kept.Count > MaxKept)
        {
            Thumbs.Remove(kept[^1]);
            kept.RemoveAt(kept.Count - 1);
            if (_results.Children.Count > kept.Count) _results.Children.RemoveAt(_results.Children.Count - 1);
        }
        SaveHistory(c.Path, kept);
        _status.Text = $"✨ Варіантів: {suggestions.Count} (від {model}). «Застосувати» — і стиль на фото.";
    }

    /// <summary>Готові правки варіанта (ШІ-маски рахуються тут же, на цьому комп'ютері) і його мініатюра.</summary>
    private (DevelopSettings Settings, Thumb Thumb, List<string> Skipped) Build(AiSuggestion suggestion)
    {
        var c = _context;
        var skipped = new List<string>();
        var settings = AiLooks.Materialize(suggestion, c.Path, c.Preview, c.Profile, skipped);
        var thumb = AiLooks.Render(c.Photo, settings, 300, crop: true);
        return (settings, thumb, skipped);
    }

    private Control Card(Look look, Thumb thumb)
    {
        var (name, description, critique, settings, skipped, steps, removed, removals) = look;
        var apply = new Button { Content = "Застосувати", Classes = { "accent" }, FontSize = 12 };
        apply.Click += (_, _) =>
        {
            // Варіант міг бути зроблений раніше: геометрія, обрізка, латки й розмиття — з фото як воно є зараз.
            var current = _context.Current;
            var applied = settings.Copy();
            applied.CopyGeometryFrom(current);
            (applied.CropX, applied.CropY, applied.CropW, applied.CropH) = (current.CropX, current.CropY, current.CropW, current.CropH);
            applied.Heals = current.Heals;
            // Pro: прибране ШІ — до латок фото (якщо цей варіант уже застосовували, вдруге не додаються).
            foreach (var spot in removals ?? EquatableList<HealSpot>.Empty)
                if (!applied.Heals.Contains(spot)) applied.Heals = applied.Heals.Add(spot);
            (applied.BlurAmount, applied.BlurRange, applied.BlurBokeh) = (current.BlurAmount, current.BlurRange, current.BlurBokeh);
            (applied.BlurFocusX, applied.BlurFocusY, applied.DepthMap) = (current.BlurFocusX, current.BlurFocusY, current.DepthMap);
            _context.Apply(applied, name);
            _status.Text = $"🎨 «{name}» на фото. Можна спробувати інший або закрити вікно.";
        };
        var info = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 0, 0) };
        info.Children.Add(new TextBlock { Text = name, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 14, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        if (critique is not null)
            info.Children.Add(new TextBlock { Text = "🧐 Після перевірки: " + critique, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        if (steps is { Count: > 0 })
            info.Children.Add(new TextBlock { Text = "📋 План: " + string.Join(" · ", steps), FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        if (removed is { Count: > 0 })
            info.Children.Add(new TextBlock { Text = "🪄 Прибрано: " + string.Join(", ", removed), FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        if (settings.Masks.Count > 0)
            info.Children.Add(new TextBlock
            {
                Text = "🖌 Маски: " + string.Join(", ", settings.Masks.Select(m => (AiMaskTypes.Of(m.Kind)?.Icon ?? (m.Kind == MaskKind.Linear ? "▭" : "◯")) + " " + m.Name)),
                FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
            });
        if (skipped.Count > 0)
            info.Children.Add(new TextBlock { Text = "Пропущено (немає моделі ШІ): " + string.Join(", ", skipped), FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { apply } });

        // Панель вузька — прев'ю до 160 px по довшій стороні, текст праворуч.
        const double side = 160;
        var image = new Image { Source = MainWindow.ToBitmap(thumb), Width = side, Height = side * thumb.Height / Math.Max(1, thumb.Width), Stretch = Stretch.Uniform };
        if (image.Height > side) (image.Width, image.Height) = (side * thumb.Width / thumb.Height, side);
        image.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(image, Dock.Left);
        row.Children.Add(image);
        row.Children.Add(info);
        return new Border { Background = new SolidColorBrush(Color.Parse("#2E2E2E")), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = row };
    }
}
