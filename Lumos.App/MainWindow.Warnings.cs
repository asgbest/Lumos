using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.ML.OnnxRuntime;

namespace Lumos.App;

/// <summary>
/// Помилки й попередження — плашкою над фото: великий текст, що переноситься, «📄 Подробиці» (журнал помилок)
/// і «✕». Сира технічна причина (на кшталт «[ErrorCode:Fail]») на екран не йде — лише у журнал.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _warningTimer;

    /// <summary>Показує плашку. Якщо є виняток — записує його в журнал і пояснює причину людською мовою.</summary>
    private void ShowWarning(string text, Exception? ex = null)
    {
        if (ex is not null)
        {
            ErrorLog.Write(text, ex);
            text += Environment.NewLine + Explain(ex);
        }
        ActionText.Text = "";
        WarningText.Text = "⚠️ " + text;
        WarningLogButton.IsVisible = ex is not null;
        WarningBanner.IsVisible = true;

        // Сама зникає за пів хвилини, щоб не заважати; закрити можна й одразу
        _warningTimer?.Stop();
        _warningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _warningTimer.Tick += (_, _) => HideWarning();
        _warningTimer.Start();
    }

    private void HideWarning()
    {
        _warningTimer?.Stop();
        WarningBanner.IsVisible = false;
    }

    private static string Explain(Exception ex) => ex switch
    {
        OnnxRuntimeException => "Збій нейромережі. Спробуй ще раз; якщо повторюється — «📄 Подробиці» покаже повний текст помилки.",
        UnauthorizedAccessException => "Немає доступу до файла чи папки.",
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
            => "Файл зайнятий іншою програмою (OneDrive, антивірус, переглядач) — спробуй ще раз за мить.",
        IOException io => io.Message,
        OutOfMemoryException => "Забракло пам'яті — закрий важкі програми й спробуй ще раз.",
        _ => ex.Message,
    };

    private void OnWarningCloseClick(object? sender, RoutedEventArgs e) => HideWarning();

    private void OnWarningLogClick(object? sender, RoutedEventArgs e)
    {
        if (!File.Exists(ErrorLog.FilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ErrorLog.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }
    }
}
