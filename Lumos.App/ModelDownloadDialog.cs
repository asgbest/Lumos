using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// «Потрібна модель ШІ»: що це, скільки важить, звідки й під якою ліцензією — і завантаження з прогресом лише після згоди.
/// Повертає true, якщо модель завантажено.
/// </summary>
public sealed class ModelDownloadDialog : Window
{
    private readonly AiModel _model;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#A0A0A0")), TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new() { Content = "Завантажити", Classes = { "accent" }, IsDefault = true };
    private readonly CancellationTokenSource _cancel = new();

    public ModelDownloadDialog(AiModel model)
    {
        _model = model;
        Title = model.CanDownload ? "⚠️ Завантажити нейромережу?" : "Потрібна модель ШІ";
        if (model.CanDownload) _download.Content = "⬇️ Так, завантажити";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));
        if (!model.CanDownload)
        {
            // Завантажувати нізвідки — замість цього відкрити папку моделей.
            _download.Content = "📁 Відкрити папку";
            _download.Click += (_, _) =>
            {
                Directory.CreateDirectory(AiModels.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AiModels.Folder) { UseShellExecute = true });
            };
        }

        var cancel = new Button { Content = model.CanDownload ? "Ні" : "Скасувати", IsCancel = true };
        cancel.Click += (_, _) =>
        {
            _cancel.Cancel();
            Close(false);
        };
        if (model.CanDownload) _download.Click += async (_, _) => await DownloadAsync();
        Closing += (_, _) => _cancel.Cancel();

        IBrush text = new SolidColorBrush(Color.Parse("#D8D8D8"));
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = model.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = text },
                new SelectableTextBlock
                {
                    Text = !model.CanDownload
                        ? $"Для цієї дії потрібна нейромережа (~{model.ApproxBytes / 1_000_000} МБ), яку не можна завантажити автоматично. " +
                          $"Поклади файл «{model.FileName}» у папку\n{AiModels.Folder}\nі спробуй ще раз."
                        : $"Для цієї дії потрібна нейромережа (~{model.ApproxBytes / 1_000_000} МБ). " +
                          "Вона завантажується один раз і далі працює без інтернету — на відеокарті, а якщо не вийде, на процесорі.",
                    TextWrapping = TextWrapping.Wrap, Foreground = text, FontSize = 12,
                },
                new Border
                {
                    IsVisible = model.CanDownload,
                    Background = new SolidColorBrush(Color.Parse("#3A3020")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#C9A227")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 4),
                    Child = new TextBlock
                    {
                        Text = $"⚠️ Буде завантажено ~{model.ApproxBytes / 1_000_000} МБ з інтернету" +
                               (model.SourceHost is { } src ? $" ({src})" : "") +
                               $" у папку {AiModels.Folder}. На лімітному чи мобільному інтернеті це може коштувати грошей.\n" +
                               "Завантажити зараз?",
                        TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#F0D890")), FontSize = 12,
                    },
                },
                new TextBlock
                {
                    Text = $"Автори: {model.Authors}\nЛіцензія: {model.License}" + (model.SourceHost is { } host ? $"\nДжерело: {host}" : ""),
                    TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#909090")), FontSize = 11,
                },
                _progress,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0), Children = { cancel, _download },
                },
            },
        };
    }

    private async Task DownloadAsync()
    {
        _download.IsEnabled = false;
        _progress.IsVisible = true;
        _status.Text = "Завантажую…";
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                long total = p.Total ?? _model.ApproxBytes;
                _progress.Value = Math.Min(100, p.Done * 100.0 / total);
                _status.Text = $"{p.Done / 1_000_000} з {total / 1_000_000} МБ";
            });
            await AiModels.DownloadAsync(_model, progress, _cancel.Token);
            Close(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            _status.Text = $"⚠️ Не вдалося завантажити: {ex.Message}";
            _download.IsEnabled = true;
            _download.Content = "Спробувати ще";
        }
    }
}
