using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumos.Core;

/// <summary>
/// Незмінний список, що порівнюється за вмістом (а не за посиланням) — щоб правки з масками й мазками пензля
/// коректно порівнювались в історії Ctrl+Z і при збереженні. У JSON — звичайний масив.
/// </summary>
[JsonConverter(typeof(EquatableListConverterFactory))]
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    public static EquatableList<T> Empty { get; } = new([]);

    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items) => _items = items.ToArray();

    public int Count => _items.Length;
    public T this[int index] => _items[index];

    public EquatableList<T> Add(T item) => new([.. _items, item]);
    public EquatableList<T> RemoveAt(int index) => new(_items.Where((_, i) => i != index));
    public EquatableList<T> SetItem(int index, T item) => new(_items.Select((x, i) => i == index ? item : x));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(EquatableList<T>? other) => other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}

internal sealed class EquatableListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<EquatableList<T>>
    {
        public override EquatableList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []);

        public override void Write(Utf8JsonWriter writer, EquatableList<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
