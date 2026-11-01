// Шейдери виконуються лише на відеокарті через DirectX 12 (Windows); вхід у них захищено перевіркою системи.
#pragma warning disable CA1416
using ComputeSharp;

namespace Lumos.Core.Gpu;

/// <summary>
/// Обробка на відеокарті (DirectX 12 через ComputeSharp). Якщо відеокарти немає, вона стара чи щось пішло не так —
/// <see cref="Device"/> null, і все рахується на процесорі, як раніше. Змінна середовища LUMOS_NO_GPU=1 вимикає відеокарту.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.2")]
public static class GpuContext
{
    private static readonly Lazy<GraphicsDevice?> LazyDevice = new(Create);
    private static volatile bool _failed;

    /// <summary>false — рахувати на процесорі (для порівняння й на випадок проблем з драйвером).</summary>
    public static bool Enabled { get; set; } = true;

    public static GraphicsDevice? Device => !Enabled || _failed ? null : LazyDevice.Value;

    public static bool Available => Device is not null;

    /// <summary>Назва відеокарти (для повідомлень).</summary>
    public static string? Name => Device?.Name;

    /// <summary>Чому відеокарту вимкнено (null — працює).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Помилка на відеокарті посеред роботи: далі — лише процесор (до перезапуску).</summary>
    public static void Disable(Exception reason)
    {
        LastError = $"{reason.GetType().Name}: {reason.Message}";
        _failed = true;
    }

    private static GraphicsDevice? Create()
    {
        if (Environment.GetEnvironmentVariable("LUMOS_NO_GPU") == "1" || !OperatingSystem.IsWindows()) return null;
        try
        {
            var device = GraphicsDevice.GetDefault();
            // Перевірка, що шейдери справді запускаються (а не лише що пристрій є).
            using var buffer = device.AllocateReadWriteBuffer(new float[] { 1, 2, 3 });
            device.For(3, new SelfTest(buffer));
            var result = buffer.ToArray();
            return result[2] == 6 ? device : null;
        }
        catch (Exception)
        {
            return null; // будь-яка проблема з відеокартою — працюємо на процесорі
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct SelfTest : IComputeShader
{
    private readonly ReadWriteBuffer<float> buffer;

    public SelfTest(ReadWriteBuffer<float> buffer) => this.buffer = buffer;

    public void Execute() => buffer[ThreadIds.X] *= 2;
}
