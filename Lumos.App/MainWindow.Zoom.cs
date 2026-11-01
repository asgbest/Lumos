using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Масштаб 100 %: рахується лише видима ділянка повнорозмірного кадру (плюс поле для ефектів),
/// один піксель фото — один піксель екрана. Тягни мишкою, щоб рухати; подвійний клік, Z або кнопка — назад до «вмістити».
/// </summary>
public partial class MainWindow
{
    /// <summary>Поле навколо видимої ділянки: ефекти (чіткість тощо) беруть сусідні пікселі, тож край не має «обриватися».</summary>
    private const int ZoomMargin = 256;

    private bool _zoomed;
    private double _zoomX, _zoomY;              // центр огляду в пікселях повнорозмірного (повернутого й обрізаного) кадру
    private Point? _panStart;
    private (double X, double Y) _panStartCenter;

    private GeometryKey? _fullGeometryKey;
    private LinearImage? _fullGeometry;
    private (LinearImage Source, int X, int Y, int W, int H)? _regionKey;
    private LinearImage? _region;

    private void InitZoom()
    {
        ZoomToggle.IsCheckedChanged += (_, _) => SetZoom(ZoomToggle.IsChecked == true, null);
        PhotoArea.DoubleTapped += (_, e) =>
        {
            if (_preview is null || _cropMode) return;
            SetZoom(!_zoomed, e.GetPosition(PhotoArea));
        };
        PhotoArea.PointerPressed += (_, e) =>
        {
            if (!_zoomed || _cropMode || !e.GetCurrentPoint(PhotoArea).Properties.IsLeftButtonPressed) return;
            _panStart = e.GetPosition(PhotoArea);
            _panStartCenter = (_zoomX, _zoomY);
            e.Pointer.Capture(PhotoArea);
        };
        PhotoArea.PointerMoved += (_, e) =>
        {
            if (_panStart is not { } start) return;
            var p = e.GetPosition(PhotoArea);
            double scaling = RenderScaling;
            _zoomX = _panStartCenter.X - (p.X - start.X) * scaling;
            _zoomY = _panStartCenter.Y - (p.Y - start.Y) * scaling;
            RequestRender();
        };
        PhotoArea.PointerReleased += (_, e) =>
        {
            if (_panStart is null) return;
            _panStart = null;
            e.Pointer.Capture(null);
            ClampZoomCenter();
        };
    }

    /// <summary>Область, де показується фото: PhotoArea без полів по 20 з кожного боку (як Margin у PhotoView).</summary>
    private Rect PhotoBox => new(20, 20, Math.Max(1, PhotoArea.Bounds.Width - 40), Math.Max(1, PhotoArea.Bounds.Height - 40));

    /// <param name="at">Точка (координати PhotoArea), яку треба показати в центрі; null — центр кадру.</param>
    private void SetZoom(bool on, Point? at)
    {
        if (on && _full is null)
        {
            // Повний кадр ще довантажується у фоні — увімкнемо, щойно буде готовий.
            if (_fullTask is { } task)
            {
                ActionText.Text = "Готую масштаб 100 %…";
                int generation = _openGeneration;
                task.ContinueWith(t =>
                {
                    if (generation != _openGeneration || t.Status != TaskStatus.RanToCompletion) return;
                    _full = t.Result;
                    ActionText.Text = "";
                    SetZoom(true, at);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            on = false;
        }
        if (on && _cropMode) SetCropMode(false);
        if (on) SplitToggle.IsChecked = false;
        if (on && !_zoomed)
        {
            var (w, h) = ImageGeometry.OutputSize(_full!.Width, _full.Height, _settings, crop: true);
            (_zoomX, _zoomY) = (w / 2.0, h / 2.0);
            if (at is { } point && _bitmap is { } bmp)
            {
                // Де на екрані лежить вписане фото → яка це точка кадру.
                var box = PhotoBox;
                double s = Math.Min(box.Width / bmp.Size.Width, box.Height / bmp.Size.Height);
                double left = box.X + (box.Width - bmp.Size.Width * s) / 2, top = box.Y + (box.Height - bmp.Size.Height * s) / 2;
                double u = Math.Clamp((point.X - left) / (bmp.Size.Width * s), 0, 1);
                double v = Math.Clamp((point.Y - top) / (bmp.Size.Height * s), 0, 1);
                (_zoomX, _zoomY) = (u * w, v * h);
            }
        }
        _zoomed = on;
        ZoomToggle.IsChecked = on;
        PhotoView.Stretch = on ? Stretch.None : Stretch.Uniform;
        PhotoArea.Cursor = on ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        RequestRender();
    }

    private (int Width, int Height) ZoomViewport()
    {
        double scaling = RenderScaling;
        var area = PhotoBox;
        return (Math.Max(64, (int)(area.Width * scaling)), Math.Max(64, (int)(area.Height * scaling)));
    }

    private void ClampZoomCenter()
    {
        if (_full is null) return;
        var (w, h) = ImageGeometry.OutputSize(_full.Width, _full.Height, _settings, crop: true);
        var (vw, vh) = ZoomViewport();
        _zoomX = w <= vw ? w / 2.0 : Math.Clamp(_zoomX, vw / 2.0, w - vw / 2.0);
        _zoomY = h <= vh ? h / 2.0 : Math.Clamp(_zoomY, vh / 2.0, h - vh / 2.0);
    }

    /// <summary>
    /// Видима ділянка повнорозмірного кадру з полем для ефектів. Обробляє лише її: ~2–3 Мп замість 24.
    /// Повертає готові байти саме видимої частини.
    /// </summary>
    private (byte[] Pixels, int Width, int Height) RenderZoomed(LinearImage full, DevelopSettings s, ToneProfile profile,
        double centerX, double centerY, int viewW, int viewH, int highlight)
    {
        var geometry = GetFullGeometry(full, s);
        int gw = geometry.Width, gh = geometry.Height;
        int vw = Math.Min(viewW, gw), vh = Math.Min(viewH, gh);
        int x0 = (int)Math.Clamp(centerX - vw / 2.0, 0, gw - vw), y0 = (int)Math.Clamp(centerY - vh / 2.0, 0, gh - vh);

        int rx = Math.Max(0, x0 - ZoomMargin), ry = Math.Max(0, y0 - ZoomMargin);
        int rw = Math.Min(gw, x0 + vw + ZoomMargin) - rx, rh = Math.Min(gh, y0 + vh + ZoomMargin) - ry;
        // Та сама ділянка — той самий об'єкт, тож кеш ефектів спрацює, поки тягнуть повзунки тону.
        var key = (geometry, rx, ry, rw, rh);
        if (_regionKey != key || _region is null)
        {
            _region = ImageGeometry.Region(geometry, rx, ry, rw, rh);
            _regionKey = key;
        }

        var bgra = new byte[rw * rh * 4];
        int fullLong = Math.Max(full.Width, full.Height);
        PhotoRenderer.Render(_region, s, profile, bgra, _histogram, new RenderFrame(1, fullLong, rx, ry, gw, gh), _zoomCache, highlight);

        var visible = new byte[vw * vh * 4];
        int ox = x0 - rx, oy = y0 - ry;
        for (int y = 0; y < vh; y++)
            Buffer.BlockCopy(bgra, ((oy + y) * rw + ox) * 4, visible, y * vw * 4, vw * 4);
        return (visible, vw, vh);
    }

    private LinearImage GetFullGeometry(LinearImage full, DevelopSettings s)
    {
        var key = new GeometryKey(full, s.Rotation, s.FlipH, WarpParams.Of(s), s.CropX, s.CropY, s.CropW, s.CropH, s.Heals, GeometryKey.BlurOf(s));
        if (key != _fullGeometryKey || _fullGeometry is null)
        {
            _fullGeometry = ImageGeometry.Apply(full, s, crop: true);
            _fullGeometryKey = key;
        }
        return _fullGeometry;
    }
}
