using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lumos.App;

/// <summary>
/// «❓ Довідка» (F1): розділи ліворуч, текст праворуч. Текст — docs\help.md, вшитий у програму; з нього ж зібрано
/// інструкцію користувача. «## » — розділ, «- » — пункт списку, «> » — порада, решта — абзаци; посилання клікабельні.
/// </summary>
public sealed partial class HelpWindow : Window
{
    private sealed record Topic(string Title, List<string> Lines);

    private readonly ListBox _topics = new() { Width = 230, Background = new SolidColorBrush(Color.Parse("#1F1F1F")) };
    private readonly StackPanel _page = new() { Margin = new Thickness(24, 18), Spacing = 8 };
    private readonly ScrollViewer _scroll;

    public HelpWindow(string? topic = null)
    {
        Title = "Довідка Lumos";
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#262626"));

        var topics = Parse(Load());
        _topics.ItemsSource = topics.Select(t => t.Title).ToList();
        _topics.SelectionChanged += (_, _) =>
        {
            if (_topics.SelectedIndex >= 0) Show(topics[_topics.SelectedIndex]);
        };
        _scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var about = new Button { Content = "ℹ️ Про програму", Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center };
        about.Click += (_, _) => new AboutWindow().ShowDialog(this);
        var left = new DockPanel { Children = { about, _topics } };
        DockPanel.SetDock(about, Dock.Bottom);
        var leftBorder = new Border { Child = left, BorderBrush = new SolidColorBrush(Color.Parse("#333")), BorderThickness = new Thickness(0, 0, 1, 0) };
        DockPanel.SetDock(leftBorder, Dock.Left);
        Content = new DockPanel { Children = { leftBorder, _scroll } };

        _topics.SelectedIndex = Math.Max(0, topics.FindIndex(t => topic is not null && t.Title.Contains(topic, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lumos.Help");
        return stream is null ? "## Довідка\nДовідку не знайдено в програмі." : new StreamReader(stream).ReadToEnd();
    }

    private static List<Topic> Parse(string markdown)
    {
        var topics = new List<Topic>();
        // Коментарі <!-- … --> — для авторів довідки, не для людини.
        markdown = Regex.Replace(markdown, "<!--.*?-->", "", RegexOptions.Singleline);
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("## ")) topics.Add(new Topic(line[3..].Trim(), []));
            else if (topics.Count > 0) topics[^1].Lines.Add(line);
        }
        return topics;
    }

    private void Show(Topic topic)
    {
        _page.Children.Clear();
        _page.Children.Add(new TextBlock { Text = topic.Title, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        foreach (string line in topic.Lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("- "))
                _page.Children.Add(new DockPanel
                {
                    Margin = new Thickness(6, 0, 0, 0),
                    Children = { Bullet(), Text(line[2..], "#D8D8D8") },
                });
            else if (line.StartsWith("> "))
                _page.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#2E2B45")), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
                    Child = Text("💡 " + line[2..], "#ECEAFF"),
                });
            else _page.Children.Add(Text(line, "#E0E0E0"));
        }
        _scroll.Offset = default;
    }

    private static TextBlock Bullet()
    {
        var bullet = new TextBlock { Text = "•", Foreground = new SolidColorBrush(Color.Parse("#8C84F0")), Width = 16, FontSize = 14 };
        DockPanel.SetDock(bullet, Dock.Left);
        return bullet;
    }

    /// <summary>Абзац; адреси https://… — підкреслені посилання, що відкриваються в браузері.</summary>
    private static SelectableTextBlock Text(string text, string color)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Foreground = new SolidColorBrush(Color.Parse(color)) };
        int at = 0;
        foreach (Match link in Links().Matches(text))
        {
            if (link.Index > at) block.Inlines!.Add(new Run(text[at..link.Index]));
            string url = link.Value;
            var run = new TextBlock
            {
                Text = url, Foreground = new SolidColorBrush(Color.Parse("#9C95FF")), TextDecorations = TextDecorations.Underline,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), FontSize = 14,
            };
            run.PointerPressed += (_, _) => Open(url);
            block.Inlines!.Add(new InlineUIContainer(run));
            at = link.Index + link.Length;
        }
        if (at < text.Length) block.Inlines!.Add(new Run(text[at..]));
        return block;
    }

    internal static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    [GeneratedRegex(@"https://[^\s)]+")]
    private static partial Regex Links();
}
