namespace Lumos.Core;

/// <summary>Результат «✨ AI Pro»: варіанти, план, за яким вони зроблені, і що прибрано ШІ-видаленням (у всіх варіантах).</summary>
public sealed record AiProResult(List<AiSuggestion> Looks, AiStylist.AiPlan Plan, List<(string Name, HealSpot Spot)> Removed);

/// <summary>
/// «✨ AI Pro» — обробка, як у розборі професіонала:
/// 1) Lumos сам знаходить ділянки кадру (небо, гори, вода, об'єкт…) і міряє їх, зокрема скільки хмар ховається в RAW;
/// 2) Gemini складає план: історія кадру, куди вести погляд, загальні кроки, 4–8 локальних кроків і що прибрати;
/// 3) Lumos прибирає відволікання (LaMa), Gemini робить варіанти, де кожен локальний крок — своя маска;
/// 4) перевірка: рендери з вимірюваннями загалом і по ділянках (небо повернулось? об'єкт виділився?) — і виправлення.
/// </summary>
public static class AiPro
{
    public const int MaxReviews = 2;

    public static async Task<AiProResult> RunAsync(string apiKey, string model, Photo photo, string path, LinearImage preview,
        ToneProfile profile, DevelopSettings current, string? wish, int count, bool review, Action<string> status, CancellationToken cancel)
    {
        var types = AiMaskTypes.All.Where(t => AiModels.IsInstalled(t.Model)).ToList();
        status("🗺 Lumos шукає на фото небо, гори, воду, об'єкт… і міряє їх");
        var (map, original, originalMetrics) = await Task.Run(() =>
        {
            var m = RegionMap.Build(path, photo, preview, current, profile, types, cancel);
            var (bgra, w, h) = AiLooks.RenderAsShot(path, preview, current, profile, withHeals: true);
            return (m, AiLooks.ToJpeg(bgra, w, h, AiStylist.ImageSide), LookMetrics.Measure(bgra, w, h));
        }, cancel);

        status("📋 Gemini складає план обробки…");
        string planAnswer = await AiStylist.AskGeminiAsync(apiKey, model, AiStylist.PlanInstruction(count),
            AiStylist.ProParts(photo, current, wish, original, map, null), cancel);
        var plan = AiStylist.ParsePlan(planAnswer);

        var removed = new List<(string Name, HealSpot Spot)>();
        if (plan.Removals.Count > 0 && AiModels.IsInstalled(AiModels.Inpaint))
        {
            status($"🪄 Прибираю: {string.Join(", ", plan.Removals.Select(r => r.Name))}…");
            removed = await Task.Run(() => AiLooks.Remove(plan.Removals, preview, current, cancel), cancel);
        }
        // Варіанти будуються від кадру вже без відволікань: латки — частина «поточних» правок для розбору відповіді.
        var basis = current.Copy();
        foreach (var (_, spot) in removed) basis.Heals = basis.Heals.Add(spot);

        status($"🎨 Gemini робить {count} варіанти за планом ({plan.Steps.Count} локальних кроків)…");
        string system = AiStylist.SystemInstruction(types.Select(t => t.Kind), count, pro: true);
        string answer = await AiStylist.AskGeminiAsync(apiKey, model, system,
            AiStylist.ProParts(photo, current, wish, original, map, plan.Json), cancel);
        var looks = AiStylist.Parse(answer, basis, path, AiStylist.MaxMasksPro);

        for (int round = 1; review && round <= MaxReviews; round++)
        {
            status(round == 1 ? $"📏 Рендерю й міряю {looks.Count} варіанти — загалом і по ділянках…" : "📏 Перевіряю виправлені варіанти…");
            var currentLooks = looks;
            var renders = await Task.Run(() => currentLooks.Select(s =>
            {
                var settings = AiLooks.Materialize(s, path, preview, profile, []);
                var full = AiLooks.Render(photo, settings, AiStylist.RenderSide, crop: false);
                return (Look: s, Jpeg: AiLooks.ToJpeg(full.Bgra, full.Width, full.Height, AiStylist.RenderSide),
                    Metrics: LookMetrics.Measure(full.Bgra, full.Width, full.Height), Regions: map.Measure(full.Bgra, full.Width, full.Height));
            }).ToList(), cancel);
            int flawed = renders.Count(r => r.Metrics.Problems(originalMetrics).Count + map.Problems(r.Regions).Count > 0);
            if (round > 1 && flawed == 0) break;
            status(round == 1 ? "🧐 Gemini перевіряє результат і виправляє…" : $"🧐 У {flawed} з {renders.Count} варіантів ще є огріхи — Gemini виправляє вдруге…");
            string refined = await AiStylist.AskGeminiAsync(apiKey, model, system,
                AiStylist.RefineParts(original, originalMetrics, renders.Select(r => (r.Look, r.Jpeg, r.Metrics)).ToList(), wish,
                    (map, renders.Select(r => r.Regions).ToList(), plan.Json)), cancel);
            looks = AiStylist.Parse(refined, basis, path, AiStylist.MaxMasksPro);
        }
        return new AiProResult(looks, plan, removed);
    }
}
