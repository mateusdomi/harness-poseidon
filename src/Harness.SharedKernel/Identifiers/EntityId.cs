using System.Diagnostics.CodeAnalysis;
using Harness.SharedKernel.Time;

namespace Harness.SharedKernel.Identifiers;

[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The tag type is the compile-time identity boundary and owns its factories.")]
public readonly record struct EntityId<TTag>(UlidValue Value) : IComparable<EntityId<TTag>>
    where TTag : notnull
{
    public bool IsEmpty => Value.IsEmpty;

    public static EntityId<TTag> New(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new EntityId<TTag>(UlidValue.New(clock.UtcNow));
    }

    public static EntityId<TTag> Parse(string text)
    {
        return new EntityId<TTag>(UlidValue.Parse(text));
    }

    public static bool TryParse(string? text, out EntityId<TTag> id)
    {
        var parsed = UlidValue.TryParse(text, out var value);
        id = parsed ? new EntityId<TTag>(value) : default;
        return parsed;
    }

    public int CompareTo(EntityId<TTag> other) => Value.CompareTo(other.Value);

    public static bool operator <(EntityId<TTag> left, EntityId<TTag> right) => left.CompareTo(right) < 0;

    public static bool operator <=(EntityId<TTag> left, EntityId<TTag> right) => left.CompareTo(right) <= 0;

    public static bool operator >(EntityId<TTag> left, EntityId<TTag> right) => left.CompareTo(right) > 0;

    public static bool operator >=(EntityId<TTag> left, EntityId<TTag> right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();
}
