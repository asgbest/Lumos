using System.Security.Cryptography;

namespace Lumos.Core;

/// <summary>
/// Нейромережа для ШІ-маски: звідки завантажити (null — автоматично не завантажується), скільки важить,
/// чия й під якою ліцензією. ImageNet — нормалізація входу за статистикою ImageNet; Channels — скільки масок видає за раз.
/// </summary>
public sealed record AiModel(string Title, string FileName, string? Url, long ApproxBytes, string? Md5, string License, string Authors, int InputSize)
{
    public bool ImageNet { get; init; }
    public int Channels { get; init; } = 1;
}

/// <summary>
/// Моделі ШІ-масок. Не входять у програму (сотні мегабайтів) — завантажуються один раз, коли вперше знадобляться,
/// у %LocalAppData%\Lumos\Models.
/// </summary>
public static class AiModels
{
    /// <summary>IS-Net «general use» з проєкту DIS (Xuebin Qin та ін.), Apache-2.0 — головний об'єкт кадру.</summary>
    public static readonly AiModel Subject = new(
        "Виділення об'єкта (IS-Net)", "isnet-general-use.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
        178_000_000, "fc16ebd8b0c10d971d3513d564d01e29", "Apache-2.0", "Xuebin Qin та ін. (DIS, github.com/xuebinqin/DIS)", 1024);

    /// <summary>U²-Net для неба (xiongzhu666, Sky-Segmentation-and-Post-processing), MIT.</summary>
    public static readonly AiModel Sky = new(
        "Виділення неба (U²-Net skyseg)", "skyseg.onnx",
        "https://huggingface.co/JianyuanWang/skyseg/resolve/main/skyseg.onnx",
        176_000_000, null, "MIT", "xiongzhu666 (github.com/xiongzhu666/Sky-Segmentation-and-Post-processing)", 320) { ImageNet = true };

    /// <summary>
    /// UperNet + ConvNeXt-Small, навчена на ADE20K (OpenMMLab, huggingface.co/openmmlab/upernet-convnext-small), MIT.
    /// Перетворена в ONNX так, що видає одразу 6 груп класів (див. <see cref="AiMaskTypes"/>): вода, рослинність, гори,
    /// будівлі, природний ґрунт, штучне покриття. Готового ONNX-файлу в мережі немає — його треба покласти в папку моделей.
    /// </summary>
    public static readonly AiModel Landscape = new(
        "Ландшафт (UperNet ConvNeXt-Small, ADE20K)", "landscape-upernet-convnext-small.onnx", null,
        324_000_000, "a70d8b4bfaf3507a3eb51478fc3db63c", "MIT", "OpenMMLab (huggingface.co/openmmlab/upernet-convnext-small)", 768)
    { ImageNet = true, Channels = 6 };

    /// <summary>
    /// LaMa (big-lama, Суворов та ін., Samsung AI Center) у ONNX від Carve, Apache-2.0 — «ШІ-видалення»: домальовує фон
    /// замість прибраного предмета. Вхід 512×512: фото RGB 0…1 і маска (1 — що прибрати); вихід RGB 0…255.
    /// </summary>
    public static readonly AiModel Inpaint = new(
        "ШІ-видалення (LaMa)", "lama_fp32.onnx",
        "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx",
        208_044_816, "2777748dc5275b27dafc63c5d4f1f730", "Apache-2.0",
        "Roman Suvorov та ін. (LaMa, github.com/advimman/lama); ONNX — Carve (huggingface.co/Carve/LaMa-ONNX)", 512);

    /// <summary>
    /// Depth Anything V2 Small (Lihe Yang та ін.), ONNX від onnx-community, Apache-2.0 — карта глибини для «Розмиття об'єктива».
    /// Вхід — фото зі сторонами, кратними 14 (коротша — 518), ImageNet-нормалізація; вихід — «близькість» (більше — ближче).
    /// </summary>
    public static readonly AiModel Depth = new(
        "Карта глибини (Depth Anything V2 Small)", "depth-anything-v2-small.onnx",
        "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx",
        99_060_839, "b06a70d234527236bdd08c00827b893d", "Apache-2.0",
        "Lihe Yang та ін. (Depth Anything V2, github.com/DepthAnything/Depth-Anything-V2); ONNX — onnx-community", 518)
    { ImageNet = true };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumos", "Models");

    public static string PathOf(AiModel model) => Path.Combine(Folder, model.FileName);

    public static bool IsInstalled(AiModel model) => File.Exists(PathOf(model));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Завантажує модель у тимчасовий файл і лише після повного (і, де є контрольна сума, перевіреного) завантаження
    /// кладе на місце — обірване завантаження не лишить «биту» модель.
    /// </summary>
    public static async Task DownloadAsync(AiModel model, IProgress<(long Done, long? Total)> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        string target = PathOf(model), temp = target + ".download";
        try
        {
            if (model.Url is null) throw new InvalidOperationException("Цю модель не можна завантажити автоматично.");
            using var response = await Http.GetAsync(model.Url,HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancel))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress.Report((done, total));
                }
            }
            if (model.Md5 is { } expected)
            {
                await using var check = File.OpenRead(temp);
                string actual = Convert.ToHexStringLower(await MD5.HashDataAsync(check, cancel));
                if (actual != expected)
                    throw new InvalidDataException($"Файл моделі пошкоджено під час завантаження (контрольна сума {actual} замість {expected}).");
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>
/// Вид ШІ-маски: яка модель і який її канал, як показувати. Find — що шукаємо («воду»), Missing — чого не знайдено («Води»).
/// Landscape — маска з групи «Ландшафт».
/// </summary>
public sealed record AiMaskType(MaskKind Kind, AiModel Model, int Channel, string Icon, string Name, string Find, string Missing, string Tip, bool Landscape);

public static class AiMaskTypes
{
    public static readonly IReadOnlyList<AiMaskType> All =
    [
        new(MaskKind.Sky, AiModels.Sky, 0, "☁️", "Небо", "небо", "Неба", "ШІ сам знайде небо", false),
        new(MaskKind.Subject, AiModels.Subject, 0, "👤", "Об'єкт", "головний об'єкт", "Об'єкта", "ШІ сам виділить головний об'єкт кадру", false),
        new(MaskKind.Water, AiModels.Landscape, 0, "💧", "Вода", "воду", "Води", "Море, річка, озеро, басейн, водоспад", true),
        new(MaskKind.Vegetation, AiModels.Landscape, 1, "🌿", "Рослинність", "рослинність", "Рослинності", "Дерева, трава, кущі, квіти", true),
        new(MaskKind.Mountains, AiModels.Landscape, 2, "⛰️", "Гори", "гори", "Гір", "Гори, пагорби, скелі", true),
        new(MaskKind.Architecture, AiModels.Landscape, 3, "🏛", "Будівлі", "будівлі", "Будівель", "Будинки, хмарочоси, вежі, мости", true),
        new(MaskKind.NaturalGround, AiModels.Landscape, 4, "🏜", "Ґрунт", "ґрунт", "Ґрунту", "Земля, пісок, поле, ґрунтова дорога", true),
        new(MaskKind.ArtificialGround, AiModels.Landscape, 5, "🛣", "Покриття", "дороги й підлогу", "Доріг і підлоги", "Дорога, тротуар, стежка, підлога, сходи", true),
    ];

    public static AiMaskType? Of(MaskKind kind) => All.FirstOrDefault(t => t.Kind == kind);
}
