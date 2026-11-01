using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Lumos.App;

/// <summary>
/// «Чекаємо на Gemini» у вікнах «✨ AI»: поверх результатів — малюнок Lumos з діафрагмою, що дихає (<see cref="SplashArt"/>),
/// і скільки часу минуло (відповідь з перевіркою йде 1–2,5 хв, тож видно, що програма не зависла).
/// </summary>
public sealed class AiWaiting : Border
{
    private readonly TextBlock _elapsed = new()
    {
        FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#A0A0A0")), HorizontalAlignment = HorizontalAlignment.Center,
    };
    // Анімація йде, лише поки видно сам малюнок (його IsVisible), тож вмикаємо й вимикаємо його разом із панеллю.
    private readonly SplashArt _art = new() { RoundAllCorners = true, IsVisible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _clock = new();

    public AiWaiting()
    {
        IsVisible = false;
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x26, 0x26, 0x26)); // попередні варіанти ледь видно під ним
        _timer.Tick += (_, _) => ShowElapsed();
        Child = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10,
            Children =
            {
                new Border
                {
                    Width = 220, Height = 256, CornerRadius = new CornerRadius(12),
                    BoxShadow = BoxShadows.Parse("0 6 24 0 #80000000"),
                    Child = _art,
                },
                _elapsed,
            },
        };
    }

    public void Start()
    {
        _clock.Restart();
        ShowElapsed();
        _timer.Start();
        IsVisible = _art.IsVisible = true;
    }

    public void Stop()
    {
        _timer.Stop();
        _clock.Stop();
        IsVisible = _art.IsVisible = false;
    }

    private void ShowElapsed() => _elapsed.Text = $"Gemini думає… {_clock.Elapsed:m\\:ss}";
}
