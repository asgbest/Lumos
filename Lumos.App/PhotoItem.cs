using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Lumos.Core;

namespace Lumos.App;

/// <summary>Фото в бібліотеці: мініатюра, рейтинг, позначка, чи є правки.</summary>
public sealed class PhotoItem(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);

    public Bitmap? Thumbnail
    {
        get;
        set => Set(ref field, value);
    }

    public int Rating
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Changed(nameof(Badges));
                Changed(nameof(HasBadges));
            }
        }
    }

    public PhotoFlag Flag
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Changed(nameof(Badges));
                Changed(nameof(HasBadges));
                Changed(nameof(ThumbOpacity));
            }
        }
    }

    public bool Edited
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>Підпис на мініатюрі: позначка й зірочки.</summary>
    public string Badges =>
        (Flag == PhotoFlag.Pick ? "⚑ " : Flag == PhotoFlag.Reject ? "✕ " : "") + new string('★', Rating);

    public bool HasBadges => Rating > 0 || Flag != PhotoFlag.None;

    /// <summary>Відхилені — притемнені, як у Lightroom.</summary>
    public double ThumbOpacity => Flag == PhotoFlag.Reject ? 0.35 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
