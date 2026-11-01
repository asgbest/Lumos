using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lumos.Core;

namespace Lumos.App;

/// <summary>
/// Масштаб, як у Lightroom: «вмістити» або рівні 25–400 % (<see cref="ZoomLevels"/>). Рахується лише видима ділянка
/// повнорозмірного кадру (плюс поле для ефектів): до 100 % — з кадру, зменшеного рівно в 2–4 рази, від 100 % — піксель
/// у піксель, а більше 100 % — ті самі пікселі, просто збільшені (без розмиття, щоб було видно кожен).
/// Коліщатко — ближче/далі до точки під курсором; тягни мишкою, щоб рухати; подвійний клік або Z — «вмістити» ↔ 100 %;
/// Ctrl+= / Ctrl+− — крок масштабу, Ctrl+0 — «вмістити».
/// </summary>
public partial class MainWindow
{
    /// <summary>Поле навколо видимої ділянки: ефекти (чіткість тощо) беруть сусідні пікселі, тож край не має «обриватися».</summary>
    private const int ZoomMargin = 256;

    /// <summary>Рівні масштабу (екранних пікселів на піксель фото). Менші за 100 % — лише 1/n: кадр зменшується рівно в n разів.</summary>
    private static readonly double[] ZoomLevels = [1 / 4.0, 1 / 3.0, 1 / 2.0, 1, 2, 3, 4];

    private bool _zoomed;
    private double _zoomScale = 1;              // поточний рівень, коли _zoomed
    private double _zoomX, _zoomY;              // центр огляду в пікселях повнорозмірного (повернутого й обрізаного) кадру
    private Point? _panStart;
    private (double X, double Y) _panStartCenter;

    private GeometryKey? _fullGeometryKey;
    private LinearImage? _fullGeometry;
    private (LinearImage Source, int Factor)? _scaledKey;
    private LinearImage? _scaledGeometry;
    private (LinearImage Source, int X, int Y, int W, int H)? _regionKey;
    private LinearImage? _region;

    private void InitZoom()
    {
        ZoomToggle.IsCheckedChanged += (_, _) =>
        {
            if ((ZoomToggle.IsChecked == true) != _zoomed) SetZoom(ZoomToggle.IsChecked == true, null);
        };
        PhotoArea.DoubleTapped += (_, e) =>
        {
            if (_preview is null || _cropMode) return;
            SetZoom(!_zoomed, e.GetPosition(PhotoArea));
        };
        PhotoArea.PointerWheelChanged += (_, e) =>
        {
            if (_preview is null || _cropMode || !ZoomToggle.IsEnabled || e.Delta.Y == 0) return;
            StepZoom(e.Delta.Y > 0 ? +1 : -1, e.GetPosition(PhotoArea));
            e.Handled = true;
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
            double toFull = RenderScaling / _zoomScale; // екранна точка → пікселі фото
            _zoomX = _panStartCenter.X - (p.X - start.X) * toFull;
            _zoomY = _panStartCenter.Y - (p.Y - start.Y) * toFull;
            ClampZoomCenter();
            RequestRender();
        };
        PhotoArea.PointerReleased += (_, e) =>
        {
            if (_panStart is null) return;
            _panStart = null;
            e.Pointer.Capture(null);
        };
    }

    /// <summary>Область, де показується фото: PhotoArea без полів по 20 з кожного боку (як Margin у PhotoView).</summary>
    private Rect PhotoBox => new(20, 20, Math.Max(1, PhotoArea.Bounds.Width - 40), Math.Max(1, PhotoArea.Bounds.Height - 40));

    /// <summary>«Вмістити» ↔ 100 %. <paramref name="at"/> — точка (координати PhotoArea), яка лишиться під курсором.</summary>
    private void SetZoom(bool on, Point? at) => ZoomTo(on ? 1 : 0, at);

    /// <summary>Крок масштабу (коліщатко, Ctrl+= / Ctrl+−): +1 — ближче, −1 — далі; менше за найменший рівень — «вмістити».</summary>
    private void StepZoom(int direction, Point? at)
    {
        if (_full is null && _fullTask is null) return;
        double fit = FitScale();
        double current = _zoomed ? _zoomScale : fit;
        // Лише рівні, ближчі за «вмістити»: дрібніші показали б кадр меншим за вікно.
        var levels = ZoomLevels.Where(l => l > fit * 1.05).ToList();
        double next = direction > 0
            ? levels.FirstOrDefault(l => l > current * 1.01, 0)
            : levels.LastOrDefault(l => l < current * 0.99, 0);
        if (direction > 0 && next == 0) return; // уже найближче
        ZoomTo(next, at);
    }

    /// <param name="scale">0 — «вмістити»; інакше рівень із <see cref="ZoomLevels"/>.</param>
    /// <param name="at">Точка (координати PhotoArea), яка лишиться під курсором; null — центр того, що видно.</param>
    private void ZoomTo(double scale, Point? at)
    {
        bool on = scale > 0;
        if (on && _full is null)
        {
            // Повний кадр ще довантажується у фоні — увімкнемо, щойно буде готовий.
            if (_fullTask is { } task)
            {
                ActionText.Text = "Готую масштаб…";
                int generation = _openGeneration;
                task.ContinueWith(t =>
                {
                    if (generation != _openGeneration || t.Status != TaskStatus.RanToCompletion) return;
                    _full = t.Result;
                    ActionText.Text = "";
                    ZoomTo(scale, at);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            on = false;
        }
        if (on && _cropMode) SetCropMode(false);
        if (on) SplitToggle.IsChecked = false;
        if (on)
        {
            var (w, h) = ImageGeometry.OutputSize(_full!.Width, _full.Height, _settings, crop: true);
            var box = PhotoBox;
            var point = at ?? box.Center;
            // Яка точка фото зараз під курсором — вона ж лишиться під ним і після зміни масштабу.
            double fx, fy;
            if (_zoomed)
            {
                double toFull = RenderScaling / _zoomScale;
                (fx, fy) = (_zoomX + (point.X - box.Center.X) * toFull, _zoomY + (point.Y - box.Center.Y) * toFull);
            }
            else if (at is not null && PhotoView.Source is Bitmap bmp)
            {
                // Де на екрані лежить вписане фото → яка це точка кадру.
                double s = Math.Min(box.Width / bmp.Size.Width, box.Height / bmp.Size.Height);
                double left = box.X + (box.Width - bmp.Size.Width * s) / 2, top = box.Y + (box.Height - bmp.Size.Height * s) / 2;
                fx = Math.Clamp((point.X - left) / (bmp.Size.Width * s), 0, 1) * w;
                fy = Math.Clamp((point.Y - top) / (bmp.Size.Height * s), 0, 1) * h;
            }
            else (fx, fy) = (w / 2.0, h / 2.0);

            double toFullNew = RenderScaling / scale;
            _zoomX = fx - (point.X - box.Center.X) * toFullNew;
            _zoomY = fy - (point.Y - box.Center.Y) * toFullNew;
            _zoomScale = scale;
        }
        _zoomed = on;
        if (on) ClampZoomCenter();
        ZoomToggle.IsChecked = on;
        ZoomToggle.Content = on ? $"🔍 {Math.Round(_zoomScale * 100)}%" : "🔍 100%";
        if (!on)
        {
            // Одразу «вписати», не чекаючи рендеру: інакше прев'ю іншого фото на мить покажеться в розмірі масштабу.
            PhotoView.Width = PhotoView.Height = double.NaN;
            PhotoView.Stretch = Stretch.Uniform;
        }
        // Більше 100 % — пікселі як є (без згладжування), щоб розгледіти різкість і шум.
        RenderOptions.SetBitmapInterpolationMode(PhotoView, on && _zoomScale > 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        PhotoArea.Cursor = on ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        RequestRender();
    }

    /// <summary>Масштаб «вмістити»: екранних пікселів на піксель повнорозмірного кадру.</summary>
    private double FitScale()
    {
        int w, h;
        if (_full is not null) (w, h) = ImageGeometry.OutputSize(_full.Width, _full.Height, _settings, crop: true);
        else if (_fullLong > 0 && PhotoView.Source is Bitmap bmp)
        {
            double k = _fullLong / Math.Max(bmp.Size.Width, bmp.Size.Height);
            (w, h) = ((int)(bmp.Size.Width * k), (int)(bmp.Size.Height * k));
        }
        else return 1;
        var (vw, vh) = ZoomViewport();
        return Math.Min(1.0 * vw / w, 1.0 * vh / h);
    }

    /// <summary>Розмір області фото в екранних пікселях.</summary>
    private (int Width, int Height) ZoomViewport()
    {
        double scaling = RenderScaling;
        var area = PhotoBox;
        return (Math.Max(64, (int)(area.Width * scaling)), Math.Max(64, (int)(area.Height * scaling)));
    }

    /// <summary>Центр не дає виїхати за край кадру; кадр, менший за вікно, — посередині.</summary>
    private void ClampZoomCenter()
    {
        if (_full is null) return;
        var (w, h) = ImageGeometry.OutputSize(_full.Width, _full.Height, _settings, crop: true);
        var (vw, vh) = ZoomViewport();
        double sw = vw / _zoomScale, sh = vh / _zoomScale; // скільки пікселів фото видно
        _zoomX = w <= sw ? w / 2.0 : Math.Clamp(_zoomX, sw / 2, w - sw / 2);
        _zoomY = h <= sh ? h / 2.0 : Math.Clamp(_zoomY, sh / 2, h - sh / 2);
    }

    /// <summary>
    /// Видима ділянка кадру з полем для ефектів. Обробляє лише її: ~2–3 Мп замість 24. Повертає готові байти саме видимої
    /// частини; кожен її піксель на екрані займає <c>max(1, scale)</c> екранних пікселів.
    /// </summary>
    /// <param name="scale">Рівень масштабу (екранних пікселів на піксель фото).</param>
    /// <param name="viewW">Ширина області фото в екранних пікселях.</param>
    private (byte[] Pixels, int Width, int Height) RenderZoomed(LinearImage full, DevelopSettings s, ToneProfile profile, double scale,
        double centerX, double centerY, int viewW, int viewH, int highlight)
    {
        // Менше 100 % — з кадру, зменшеного рівно в factor разів; тоді піксель зображення = піксель екрана.
        int factor = scale < 1 ? (int)Math.Round(1 / scale) : 1;
        var geometry = GetFullGeometry(full, s);
        if (factor > 1)
        {
            if (_scaledKey != (geometry, factor) || _scaledGeometry is null)
            {
                _scaledGeometry = geometry.DownscaleBy(factor);
                _scaledKey = (geometry, factor);
            }
            geometry = _scaledGeometry;
        }
        double magnify = Math.Max(1, scale);
        int gw = geometry.Width, gh = geometry.Height;
        int vw = Math.Min((int)Math.Ceiling(viewW / magnify), gw), vh = Math.Min((int)Math.Ceiling(viewH / magnify), gh);
        double cx = centerX / factor, cy = centerY / factor;
        int x0 = (int)Math.Clamp(cx - vw / 2.0, 0, gw - vw), y0 = (int)Math.Clamp(cy - vh / 2.0, 0, gh - vh);

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
        PhotoRenderer.Render(_region, s, profile, bgra, _histogram, new RenderFrame(1.0 / factor, fullLong, rx, ry, gw, gh), _zoomCache, highlight);

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
