using System.Text.Json.Serialization;

namespace Lumos.Core;

/// <summary>Вісім кольорів змішувача HSL, як у Lightroom.</summary>
public enum HslBand { Red, Orange, Yellow, Green, Aqua, Blue, Purple, Magenta }

/// <summary>Правка одного кольору в змішувачі: відтінок, насиченість, яскравість — кожне від −100 до +100.</summary>
public readonly record struct HslAdjust(double Hue, double Saturation, double Luminance);

/// <summary>Колесо кольорового грейдування: відтінок 0…360°, сила 0…100, яскравість −100…+100.</summary>
public readonly record struct GradeWheel(double Hue, double Saturation, double Luminance);

/// <summary>
/// Параметри правок фото. Оригінал ніколи не змінюється: щоразу зображення перераховується з RAW за цими числами,
/// тож їх можна зберегти поруч із файлом (JSON/XMP), скопіювати як пресет чи відкотити.
/// Повзунки — як у Lightroom: експозиція в ступенях (EV), решта від −100 до +100, нуль — без змін.
/// </summary>
public sealed record DevelopSettings
{
    // Світло
    public double Exposure { get; set; }
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }

    // Тонова крива: загальна і для кожного каналу.
    public Curve CurveMaster { get; set; } = Curve.Identity;
    public Curve CurveRed { get; set; } = Curve.Identity;
    public Curve CurveGreen { get; set; } = Curve.Identity;
    public Curve CurveBlue { get; set; } = Curve.Identity;

    // Колір. Температура й відтінок — відносно балансу білого камери.
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Vibrance { get; set; }
    public double Saturation { get; set; }

    // Змішувач кольорів (HSL)
    public HslAdjust HslRed { get; set; }
    public HslAdjust HslOrange { get; set; }
    public HslAdjust HslYellow { get; set; }
    public HslAdjust HslGreen { get; set; }
    public HslAdjust HslAqua { get; set; }
    public HslAdjust HslBlue { get; set; }
    public HslAdjust HslPurple { get; set; }
    public HslAdjust HslMagenta { get; set; }

    // Кольорове грейдування
    public GradeWheel GradeShadows { get; set; }
    public GradeWheel GradeMidtones { get; set; }
    public GradeWheel GradeHighlights { get; set; }
    public GradeWheel GradeGlobal { get; set; }
    public double GradeBlending { get; set; } = 50;
    public double GradeBalance { get; set; }

    // Ефекти (як у Lightroom): текстура, чіткість, димка −100…+100.
    public double Texture { get; set; }
    public double Clarity { get; set; }
    public double Dehaze { get; set; }
    // Віньєтка після обрізки: сила −100…+100 (мінус — темні кути), середина/розмиття 0…100, округлість −100…+100.
    public double VignetteAmount { get; set; }
    public double VignetteMidpoint { get; set; } = 50;
    public double VignetteFeather { get; set; } = 50;
    public double VignetteRoundness { get; set; }
    // Плівкове зерно: сила, розмір, шорсткість 0…100.
    public double GrainAmount { get; set; }
    public double GrainSize { get; set; } = 25;
    public double GrainRoughness { get; set; } = 50;

    // Деталізація: різкість 0…150 (радіус 0.5…3 px, деталізація й маскування 0…100), шумозаглушення 0…100.
    public double SharpenAmount { get; set; }
    public double SharpenRadius { get; set; } = 1;
    public double SharpenDetail { get; set; } = 25;
    public double SharpenMasking { get; set; }
    public double NoiseReduction { get; set; }
    public double ColorNoiseReduction { get; set; }

    // Геометрія: поворот на 90°, віддзеркалення, вирівнювання (градуси, + за годинниковою), рамка обрізки (частки 0…1).
    public int Rotation { get; set; }
    public bool FlipH { get; set; }
    public double Angle { get; set; }
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropW { get; set; } = 1;
    public double CropH { get; set; } = 1;

    // Виправлення об'єктива: дисторсія −100…+100 (+ прибирає «бочку»), хроматичні аберації червоного й синього −100…+100,
    // віньєтування об'єктива −100…+100 (+ світлішає кути) і його середина 0…100.
    public double LensDistortion { get; set; }
    public double LensCaRed { get; set; }
    public double LensCaBlue { get; set; }
    public double LensVignette { get; set; }
    public double LensVignetteMid { get; set; } = 50;

    /// <summary>Профіль об'єктива з самого RAW (див. <see cref="Core.LensProfile"/>); ручні повзунки — поверх нього.</summary>
    public bool LensProfile { get; set; }

    /// <summary>
    /// Числа профілю цього фото (null — у файлі профілю немає). Не зберігаються, їх підставляє <see cref="LensProfiles.Attach"/>;
    /// стилі й маски їх не переносять.
    /// </summary>
    [JsonIgnore]
    public LensProfile? LensProfileData { get; set; }

    // Перспектива (як «Transform» у Lightroom): вертикаль/горизонталь/пропорції/зсув −100…+100, поворот −10…+10°, масштаб 50…150 %.
    public double PerspVertical { get; set; }
    public double PerspHorizontal { get; set; }
    public double PerspRotate { get; set; }
    public double PerspAspect { get; set; }
    public double PerspScale { get; set; } = 100;
    public double PerspX { get; set; }
    public double PerspY { get; set; }

    /// <summary>Поворот, віддзеркалення, вирівнювання, об'єктив і перспектива — з іншого набору правок (без обрізки).</summary>
    public void CopyGeometryFrom(DevelopSettings s)
    {
        (Rotation, FlipH, Angle) = (s.Rotation, s.FlipH, s.Angle);
        (LensProfile, LensProfileData) = (s.LensProfile, s.LensProfileData);
        (LensDistortion, LensCaRed, LensCaBlue, LensVignette, LensVignetteMid) =
            (s.LensDistortion, s.LensCaRed, s.LensCaBlue, s.LensVignette, s.LensVignetteMid);
        (PerspVertical, PerspHorizontal, PerspRotate, PerspAspect, PerspScale, PerspX, PerspY) =
            (s.PerspVertical, s.PerspHorizontal, s.PerspRotate, s.PerspAspect, s.PerspScale, s.PerspX, s.PerspY);
    }

    public HslAdjust GetHsl(HslBand band) => band switch
    {
        HslBand.Red => HslRed,
        HslBand.Orange => HslOrange,
        HslBand.Yellow => HslYellow,
        HslBand.Green => HslGreen,
        HslBand.Aqua => HslAqua,
        HslBand.Blue => HslBlue,
        HslBand.Purple => HslPurple,
        _ => HslMagenta,
    };

    public void SetHsl(HslBand band, HslAdjust value)
    {
        switch (band)
        {
            case HslBand.Red: HslRed = value; break;
            case HslBand.Orange: HslOrange = value; break;
            case HslBand.Yellow: HslYellow = value; break;
            case HslBand.Green: HslGreen = value; break;
            case HslBand.Aqua: HslAqua = value; break;
            case HslBand.Blue: HslBlue = value; break;
            case HslBand.Purple: HslPurple = value; break;
            default: HslMagenta = value; break;
        }
    }

    /// <summary>Маски з власними правками (див. <see cref="Mask"/>).</summary>
    public EquatableList<Mask> Masks { get; set; } = EquatableList<Mask>.Empty;

    // Розмиття об'єктива: сила 0…100, глибина різкої зони 0…100, яскравість «зайчиків» боке 0…100,
    // точка фокуса (частки полотна; −1 — сама: найближче в кадрі) і карта глибини від ШІ (PNG у відтінках сірого, 1 — найближче).
    public double BlurAmount { get; set; }
    public double BlurRange { get; set; } = 25;
    public double BlurBokeh { get; set; } = 50;
    public double BlurFocusX { get; set; } = -1;
    public double BlurFocusY { get; set; } = -1;
    public string? DepthMap { get; set; }

    /// <summary>Латки відновлювального пензля (див. <see cref="HealSpot"/>), по черзі.</summary>
    public EquatableList<HealSpot> Heals { get; set; } = EquatableList<HealSpot>.Empty;

    /// <summary>Глибока копія: правки масок теж копіюються, тож зміна маски не зачепить знімки в історії Ctrl+Z.</summary>
    public DevelopSettings Copy() => this with { Masks = new(Masks.Select(m => m.DeepCopy())) };

    /// <summary>
    /// Стартові правки фото: для RAW — як у Lightroom (різкість 40, кольорове шумозаглушення 25: сирі дані сенсора
    /// м'які й із кольоровим шумом; профіль об'єктива, якщо камера його записала), для готових фото (JPEG тощо) — нулі,
    /// щоб лишався рівно оригінал.
    /// </summary>
    public static DevelopSettings DefaultFor(string photoPath)
    {
        if (PhotoDecoder.IsRendered(photoPath)) return new();
        var profile = LensProfiles.For(photoPath);
        return new() { SharpenAmount = 40, ColorNoiseReduction = 25, LensProfile = profile is not null, LensProfileData = profile };
    }

    /// <summary>Чи фото не має власних правок (лише стартові) — тоді файл правок не потрібен.</summary>
    public bool IsDefaultFor(string photoPath) => this == DefaultFor(photoPath);
}
