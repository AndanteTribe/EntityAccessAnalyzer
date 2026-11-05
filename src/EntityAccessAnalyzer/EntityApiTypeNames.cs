using System;
using System.Collections.Generic;

namespace EntityAccessAnalyzer;

/// <summary>
/// Type names defining the entity API policy. Symbol identities are resolved per compilation.
/// </summary>
internal static class EntityApiTypeNames
{
    public const string Collection = "System.Collections.ICollection";
    public const string GenericCollection = "System.Collections.Generic.ICollection`1";

    // These groups are enumerated once per compilation to resolve symbol identities.
    public static readonly ReadOnlyMemory<string> AllowedCollections = new[]
    {
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.ImmutableList`1",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Collections.Immutable.ImmutableSortedSet`1",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableSortedDictionary`2",
        "System.Collections.Immutable.ImmutableQueue`1",
        "System.Collections.Immutable.ImmutableStack`1",
        "System.Collections.Immutable.IImmutableList`1",
        "System.Collections.Immutable.IImmutableSet`1",
        "System.Collections.Immutable.IImmutableDictionary`2",
        "System.Collections.Immutable.IImmutableQueue`1",
        "System.Collections.Immutable.IImmutableStack`1",
        "System.Collections.Frozen.FrozenSet`1",
        "System.Collections.Frozen.FrozenDictionary`2",
        "System.Collections.ObjectModel.ReadOnlyCollection`1",
        "System.Collections.ObjectModel.ReadOnlyDictionary`2",
        "System.Collections.ObjectModel.ReadOnlyObservableCollection`1",
        "System.Collections.ObjectModel.ReadOnlySet`1",
    };

    // Allowed collection elements are recursive too; these are the additional recursive types.
    public static readonly ReadOnlyMemory<string> RecursiveTypes = new[]
    {
        "System.ReadOnlyMemory`1", "System.ReadOnlySpan`1",
        "System.ValueTuple`1", "System.ValueTuple`2", "System.ValueTuple`3", "System.ValueTuple`4",
        "System.ValueTuple`5", "System.ValueTuple`6", "System.ValueTuple`7", "System.ValueTuple`8",
    };

    public static readonly ReadOnlyMemory<string> MutableTypes = new[]
    {
        "System.Memory`1", "System.Span`1", "System.ArraySegment`1",
    };

    public static readonly ReadOnlyMemory<string> EnumerationTypes = new[]
    {
        "System.Collections.IEnumerable", "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IAsyncEnumerable`1",
    };

    // Contract backports are identified by metadata name, regardless of their defining assembly.
    private static readonly HashSet<string> s_allowedMethodContracts = new(
        new[]
        {
            "System.IEquatable`1", "System.IComparable", "System.IComparable`1",
            "System.Collections.IStructuralEquatable", "System.Collections.IStructuralComparable",
            "System.Collections.IComparer", "System.Collections.Generic.IComparer`1",
            "System.Collections.IEqualityComparer", "System.Collections.Generic.IEqualityComparer`1",
            "System.IFormattable", "System.ISpanFormattable", "System.IUtf8SpanFormattable",
            "System.IParsable`1", "System.ISpanParsable`1", "System.IUtf8SpanParsable`1",
        },
        StringComparer.Ordinal);

    public static bool IsAllowedMethodContract(string metadataName) => s_allowedMethodContracts.Contains(metadataName);
}