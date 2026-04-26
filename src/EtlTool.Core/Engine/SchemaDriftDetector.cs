using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

public enum SchemaDriftKind
{
    Added,              // column present in current but absent from snapshot
    Removed,            // column present in snapshot but absent from current
    TypeChanged,        // same name, different DataType
    NullabilityChanged, // same name + type, different Nullable
    PrimaryKeyChanged,  // same name, different IsPrimaryKey
}

public sealed record SchemaDriftItem(
    string ColumnName,
    SchemaDriftKind Kind,
    string? Was,    // null for Added; human-readable prior state otherwise
    string? IsNow); // null for Removed; human-readable current state otherwise

public sealed class SchemaDriftReport
{
    public List<SchemaDriftItem> Items { get; init; } = new();
    public bool HasDrift => Items.Count > 0;

    /// <summary>
    /// Number of drift items that affect columns referenced by the active
    /// mapping.  <c>null</c> when no mapping information was supplied.
    /// Added items are excluded — a brand-new column cannot break an
    /// existing mapping.
    /// </summary>
    public int? AffectingMappedColumns { get; init; }

    public string ShortSummary() => HasDrift
        ? string.Join(", ", Items
            .GroupBy(i => i.Kind)
            .Select(g => $"{g.Count()} {g.Key}"))
        : "no drift";
}

public static class SchemaDriftDetector
{
    /// <summary>
    /// Compare <paramref name="snapshot"/> against <paramref name="current"/>
    /// and return every detected difference.
    /// </summary>
    /// <remarks>
    /// Column name matching is case-insensitive so that Oracle upper-case
    /// schemas and SQL Server mixed-case schemas round-trip correctly.
    ///
    /// When <paramref name="mappedColumnNames"/> is provided,
    /// <see cref="SchemaDriftReport.AffectingMappedColumns"/> counts how many
    /// drift items involve a column that appears in the mapping — excluding
    /// <see cref="SchemaDriftKind.Added"/> items (a new column cannot break
    /// an existing mapping).
    /// </remarks>
    public static SchemaDriftReport Compare(
        IReadOnlyList<ColumnInfo> snapshot,
        IReadOnlyList<ColumnInfo> current,
        IEnumerable<string>? mappedColumnNames = null)
    {
        // Keyed by upper-case name for case-insensitive matching (Oracle vs SQL Server).
        var snapMap = snapshot.ToDictionary(c => Normalise(c.Name), c => c);
        var currMap = current.ToDictionary(c => Normalise(c.Name), c => c);

        var items = new List<SchemaDriftItem>();
        DetectAdded(snapMap, currMap, items);
        DetectRemoved(snapMap, currMap, items);
        DetectChanged(snapMap, currMap, items);

        int? affecting = ComputeAffecting(items, mappedColumnNames);

        return new SchemaDriftReport { Items = items, AffectingMappedColumns = affecting };
    }

    // ── private helpers ─────────────────────────────────────────────────────

    private static void DetectAdded(
        Dictionary<string, ColumnInfo> snapMap,
        Dictionary<string, ColumnInfo> currMap,
        List<SchemaDriftItem> items)
    {
        foreach (var (key, col) in currMap)
            if (!snapMap.ContainsKey(key))
                items.Add(new SchemaDriftItem(col.Name, SchemaDriftKind.Added,
                    Was: null, IsNow: Describe(col)));
    }

    private static void DetectRemoved(
        Dictionary<string, ColumnInfo> snapMap,
        Dictionary<string, ColumnInfo> currMap,
        List<SchemaDriftItem> items)
    {
        foreach (var (key, col) in snapMap)
            if (!currMap.ContainsKey(key))
                items.Add(new SchemaDriftItem(col.Name, SchemaDriftKind.Removed,
                    Was: Describe(col), IsNow: null));
    }

    private static void DetectChanged(
        Dictionary<string, ColumnInfo> snapMap,
        Dictionary<string, ColumnInfo> currMap,
        List<SchemaDriftItem> items)
    {
        foreach (var (key, before) in snapMap)
        {
            if (!currMap.TryGetValue(key, out var after))
                continue;

            // Use the live column's name as the canonical display name.
            if (!string.Equals(before.DataType, after.DataType, StringComparison.OrdinalIgnoreCase))
                items.Add(new SchemaDriftItem(after.Name, SchemaDriftKind.TypeChanged,
                    Was: before.DataType, IsNow: after.DataType));

            if (before.Nullable != after.Nullable)
                items.Add(new SchemaDriftItem(after.Name, SchemaDriftKind.NullabilityChanged,
                    Was: NullableLabel(before.Nullable), IsNow: NullableLabel(after.Nullable)));

            if (before.IsPrimaryKey != after.IsPrimaryKey)
                items.Add(new SchemaDriftItem(after.Name, SchemaDriftKind.PrimaryKeyChanged,
                    Was: PkLabel(before.IsPrimaryKey), IsNow: PkLabel(after.IsPrimaryKey)));
        }
    }

    private static int? ComputeAffecting(
        List<SchemaDriftItem> items,
        IEnumerable<string>? mappedColumnNames)
    {
        if (mappedColumnNames is null)
            return null;

        var mapped = mappedColumnNames.Select(Normalise).ToHashSet();

        // Added columns cannot break existing mappings — exclude them.
        return items
            .Where(i => i.Kind != SchemaDriftKind.Added)
            .Count(i => mapped.Contains(Normalise(i.ColumnName)));
    }

    private static string Normalise(string name) => name.ToUpperInvariant();

    private static string Describe(ColumnInfo c)
    {
        var parts = new List<string> { c.DataType };
        if (c.Nullable) parts.Add("nullable"); else parts.Add("not null");
        if (c.IsPrimaryKey) parts.Add("PK");
        return string.Join(" ", parts);
    }

    private static string NullableLabel(bool nullable) =>
        nullable ? "nullable" : "not null";

    private static string PkLabel(bool pk) =>
        pk ? "PK" : "non-PK";
}
