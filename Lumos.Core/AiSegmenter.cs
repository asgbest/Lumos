using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lumos.Core;

/// <summary>
/// ШІ-маски: нейромережа знаходить небо, головний об'єкт чи частини ландшафту. Рахує на відеокарті (DirectML),
/// а якщо не вийшло — на процесорі. Мережа працює в малій роздільності (небо — 320×320, ландшафт — 768×768, об'єкт — 1024×1024),
/// тож край маски потім підганяється під краї самого фото керованим фільтром — гілки на тлі неба не розмиваються.
/// </summary>
public static class AiSegmenter
{
    /// <param name="Peak">Найбільша сила маски: близько нуля — мережа нічого не знайшла (напр. неба в кадрі немає).</param>
    public sealed record Result(float[] Mask, int Width, int Height, bool Gpu, long Milliseconds, float Peak);

    private static readonly Dictionary<string, (InferenceSession Session, bool Gpu)> Sessions = [];
    private static readonly Lock SessionLock = new();

    /// <summary>Сесія мережі: спершу відеокарта, якщо не вдалося — процесор. Створюється один раз.</summary>
    internal static (InferenceSession Session, bool Gpu) SessionFor(AiModel model)
    {
        lock (SessionLock)
        {
            if (Sessions.TryGetValue(model.FileName, out var ready)) return ready;
            string path = AiModels.PathOf(model);
            if (!CpuOnly.Contains(model.FileName))
                try
                {
                    var gpu = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                    gpu.AppendExecutionProvider_DML(0);
                    return Sessions[model.FileName] = (new InferenceSession(path, gpu), true);
                }
                catch (OnnxRuntimeException) { }
            var cpu = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            return Sessions[model.FileName] = (new InferenceSession(path, cpu), false);
        }
    }

    // Моделі, що не працюють на відеокарті (не всі операції DirectML підтримує) — лише процесор.
    // LaMa: DirectML відхиляє множення матриць у її Фур'є-блоках (перевірено на RTX 3050, ORT 1.24).
    private static readonly HashSet<string> CpuOnly = [AiModels.Inpaint.FileName];

    /// <summary>
    /// Запуск мережі; якщо відеокарта відмовила посеред роботи — та сама мережа ще раз на процесорі
    /// (і для цієї моделі надалі лише процесор).
    /// </summary>
    internal static float[] Run(AiModel model, IReadOnlyCollection<NamedOnnxValue> inputs, out bool gpu)
    {
        var (session, onGpu) = SessionFor(model);
        try
        {
            using var results = session.Run(inputs);
            gpu = onGpu;
            return results.First().AsTensor<float>().ToArray();
        }
        catch (OnnxRuntimeException) when (onGpu)
        {
            lock (SessionLock)
            {
                CpuOnly.Add(model.FileName);
                Sessions.Remove(model.FileName);
                session.Dispose();
            }
            return Run(model, inputs, out gpu);
        }
    }

    // Останній вихід моделі з кількома каналами («Ландшафт»): друга маска з того ж кадру не потребує нового проходу мережі.
    private sealed record CachedOutput(string Key, float[] Output);
    private static volatile CachedOutput? _lastOutput;

    /// <param name="bgra">Кадр, як його бачить людина (8 біт sRGB), Width × Height × 4.</param>
    /// <param name="channel">Яку з масок моделі взяти (для «Ландшафту» — група: вода, рослинність…).</param>
    /// <param name="cacheKey">Однаковий ключ — той самий кадр: вихід мережі береться з минулого разу.</param>
    /// <returns>Сила маски 0…1 у роздільності кадру.</returns>
    public static Result Segment(AiModel model, byte[] bgra, int width, int height, int channel = 0, string? cacheKey = null)
    {
        var (session, gpu) = SessionFor(model);
        var watch = Stopwatch.StartNew();
        int size = model.InputSize;
        string? key = cacheKey is null ? null : model.FileName + "|" + cacheKey;

        float[] output;
        if (key is not null && _lastOutput is { } last && last.Key == key) output = last.Output;
        else
        {
            // Вхід мережі: кадр, розтягнутий до size×size, RGB у форматі NCHW із нормалізацією, як при навчанні моделі.
            var input = new DenseTensor<float>([1, 3, size, size]);
            float[] mean = model.ImageNet ? [0.485f, 0.456f, 0.406f] : [0.5f, 0.5f, 0.5f];
            float[] std = model.ImageNet ? [0.229f, 0.224f, 0.225f] : [1f, 1f, 1f];
            Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    double sx = (x + 0.5) * width / size - 0.5, sy = (y + 0.5) * height / size - 0.5;
                    for (int c = 0; c < 3; c++)
                    {
                        float v = Sample(bgra, width, height, sx, sy, 2 - c) / 255f; // BGRA → R, G, B
                        input[0, c, y, x] = (v - mean[c]) / std[c];
                    }
                }
            });

            string inputName = session.InputMetadata.Keys.First();
            using (var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]))
                output = outputs.First().AsTensor<float>().ToArray();
            if (key is not null && model.Channels > 1) _lastOutput = new CachedOutput(key, output);
        }
        // Вихід — квадрат, не обов'язково розміром із вхід («Ландшафт» видає 1/4: 192×192).
        int side = (int)Math.Round(Math.Sqrt(output.Length / (double)model.Channels));
        float[] raw = output[(channel * side * side)..((channel + 1) * side * side)];

        // Усі мережі вже видають імовірність 0…1 (сигмоїда чи softmax). Розтягувати «від мінімуму до максимуму», як у прикладах
        // авторів, не можна: коли неба в кадрі немає, слабкий шум розтягнувся б до повної сили (світла стіна стала б «небом»).
        float min = raw.Min(), max = raw.Max();
        if (min < -0.01f || max > 1.01f)
        {
            float range = Math.Max(1e-6f, max - min);
            for (int i = 0; i < raw.Length; i++) raw[i] = (raw[i] - min) / range;
        }

        // Назад до розміру кадру й підгонка країв під саме фото.
        var mask = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
                mask[y * width + x] = SampleFloat(raw, side, side, (x + 0.5) * side / width - 0.5, (y + 0.5) * side / height - 0.5);
        });
        Refine(mask, bgra, width, height);
        return new Result(mask, width, height, gpu, watch.ElapsedMilliseconds, mask.Max());
    }

    /// <summary>
    /// Керований фільтр: маска «прилипає» до країв на фото (де різкий перехід яскравості — там і край маски),
    /// а на рівних ділянках лишається плавною.
    /// </summary>
    internal static void Refine(float[] mask, byte[] bgra, int w, int h)
    {
        int n = w * h, r = Math.Max(2, Math.Max(w, h) / 200);
        const float eps = 1e-3f;
        var guide = new float[n];
        for (int i = 0; i < n; i++) guide[i] = (0.0722f * bgra[i * 4] + 0.7152f * bgra[i * 4 + 1] + 0.2126f * bgra[i * 4 + 2]) / 255f;

        float[] meanI = (float[])guide.Clone(), meanP = (float[])mask.Clone(), corrIP = new float[n], varI = new float[n], tmp = new float[n];
        for (int i = 0; i < n; i++) { corrIP[i] = guide[i] * mask[i]; varI[i] = guide[i] * guide[i]; }
        Blur.Box(meanI, tmp, w, h, r);
        Blur.Box(meanP, tmp, w, h, r);
        Blur.Box(corrIP, tmp, w, h, r);
        Blur.Box(varI, tmp, w, h, r);
        for (int i = 0; i < n; i++)
        {
            float variance = varI[i] - meanI[i] * meanI[i], covariance = corrIP[i] - meanI[i] * meanP[i];
            float a = covariance / (variance + eps);
            corrIP[i] = a;                         // a
            varI[i] = meanP[i] - a * meanI[i];     // b
        }
        Blur.Box(corrIP, tmp, w, h, r);
        Blur.Box(varI, tmp, w, h, r);
        for (int i = 0; i < n; i++) mask[i] = Math.Clamp(corrIP[i] * guide[i] + varI[i], 0, 1);
    }

    private static float Sample(byte[] bgra, int w, int h, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = bgra[(y0 * w + x0) * 4 + channel], b = bgra[(y0 * w + x1) * 4 + channel];
        float c = bgra[(y1 * w + x0) * 4 + channel], d = bgra[(y1 * w + x1) * 4 + channel];
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    internal static float SampleFloat(float[] data, int w, int h, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = data[y0 * w + x0], b = data[y0 * w + x1], c = data[y1 * w + x0], d = data[y1 * w + x1];
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }
}
