// Шейдери виконуються лише на відеокарті через DirectX 12 (Windows); вхід у них захищено перевіркою системи.
#pragma warning disable CA1416
using System.Runtime.Versioning;
using ComputeSharp;

namespace Lumos.Core.Gpu;

/// <summary>
/// Пакет команд для відеокарти: усі кроки кадру (десятки шейдерів) відправляються разом і виконуються,
/// коли пакет закривають, — без очікування відеокарти після кожного кроку (на цьому раніше губилось ~1 мс на крок).
/// Після кожного кроку — бар'єр на всі буфери, яких він торкався: наступний крок не почне писати/читати їх раніше.
/// Тимчасові буфери (таблиці, ядра) звільняються після виконання пакета.
/// </summary>
[SupportedOSPlatform("windows6.2")]
internal sealed class GpuBatch : IDisposable
{
    private ComputeContext _context;
    private readonly List<IDisposable> _deferred = [];

    public GraphicsDevice Device { get; }

    public GpuBatch(GraphicsDevice device)
    {
        Device = device;
        _context = device.CreateComputeContext();
    }

    public void For<T>(int x, int y, in T shader, params object[] touched) where T : struct, IComputeShader, ComputeSharp.Descriptors.IComputeShaderDescriptor<T>
    {
        _context.For(x, y, in shader);
        Barriers(touched);
    }

    public void For<T>(int x, in T shader, params object[] touched) where T : struct, IComputeShader, ComputeSharp.Descriptors.IComputeShaderDescriptor<T>
    {
        _context.For(x, in shader);
        Barriers(touched);
    }

    private void Barriers(object[] touched)
    {
        foreach (var resource in touched)
        {
            if (resource is ReadWriteBuffer<float> f) _context.Barrier(f);
            else if (resource is ReadWriteBuffer<uint> u) _context.Barrier(u);
            else throw new ArgumentException($"Бар'єр для {resource.GetType().Name} не підтримується.");
        }
    }

    public void Copy(ReadWriteBuffer<float> source, ReadWriteBuffer<float> destination, int w, int h) =>
        For(w, h, new CopyPlane(source, destination), source, destination);

    /// <summary>Звільнити після виконання пакета (буфер ще потрібен шейдерам у черзі).</summary>
    public T Defer<T>(T resource) where T : IDisposable
    {
        _deferred.Add(resource);
        return resource;
    }

    /// <summary>Виконує всі команди й чекає на результат.</summary>
    public void Dispose()
    {
        try
        {
            _context.Dispose();
        }
        finally
        {
            foreach (var d in _deferred) d.Dispose();
            _deferred.Clear();
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct CopyPlane : IComputeShader
{
    private readonly ReadWriteBuffer<float> src, dst;

    public CopyPlane(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst)
    {
        this.src = src; this.dst = dst;
    }

    public void Execute()
    {
        int i = ThreadIds.Y * DispatchSize.X + ThreadIds.X;
        dst[i] = src[i];
    }
}
