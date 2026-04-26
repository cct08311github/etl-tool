using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class SchemaDriftDetectorTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static ColumnInfo Col(
        string name, string type = "VARCHAR2(100)",
        bool nullable = true, bool pk = false) =>
        new(name, type, nullable, pk);

    // ── 1. identical snapshot and current ────────────────────────────────────

    [Fact]
    public void NoDrift_WhenSnapshotEqualsCurrentExactly()
    {
        var snap = new[] { Col("Id", "NUMBER", false, true), Col("Name") };
        var curr = new[] { Col("Id", "NUMBER", false, true), Col("Name") };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.False(report.HasDrift);
        Assert.Empty(report.Items);
        Assert.Equal("no drift", report.ShortSummary());
    }

    // ── 2. current has an extra column → Added ───────────────────────────────

    [Fact]
    public void Added_WhenCurrentHasExtraColumn()
    {
        var snap = new[] { Col("Id", "NUMBER", false, true) };
        var curr = new[] { Col("Id", "NUMBER", false, true), Col("Email") };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.True(report.HasDrift);
        var item = Assert.Single(report.Items);
        Assert.Equal(SchemaDriftKind.Added, item.Kind);
        Assert.Equal("Email", item.ColumnName);
        Assert.Null(item.Was);
        Assert.NotNull(item.IsNow);
        Assert.Contains("VARCHAR2(100)", item.IsNow);
    }

    // ── 3. snapshot has an extra column (current is missing it) → Removed ────

    [Fact]
    public void Removed_WhenCurrentLacksColumn()
    {
        var snap = new[] { Col("Id", "NUMBER", false, true), Col("LegacyField") };
        var curr = new[] { Col("Id", "NUMBER", false, true) };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.True(report.HasDrift);
        var item = Assert.Single(report.Items);
        Assert.Equal(SchemaDriftKind.Removed, item.Kind);
        Assert.Equal("LegacyField", item.ColumnName);
        Assert.NotNull(item.Was);
        Assert.Null(item.IsNow);
    }

    // ── 4. same name but DataType changed → TypeChanged ──────────────────────

    [Fact]
    public void TypeChanged_WhenDataTypeChanges()
    {
        var snap = new[] { Col("Amount", "NUMBER") };
        var curr = new[] { Col("Amount", "DECIMAL(18,4)") };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.True(report.HasDrift);
        var item = Assert.Single(report.Items);
        Assert.Equal(SchemaDriftKind.TypeChanged, item.Kind);
        Assert.Equal("Amount", item.ColumnName);
        Assert.Contains("NUMBER", item.Was);
        Assert.Contains("DECIMAL(18,4)", item.IsNow);
    }

    // ── 5. same name + type but Nullable changed → NullabilityChanged ─────────

    [Fact]
    public void NullabilityChanged_WhenNullableFlips()
    {
        var snap = new[] { new ColumnInfo("Code", "CHAR(3)", Nullable: true,  IsPrimaryKey: false) };
        var curr = new[] { new ColumnInfo("Code", "CHAR(3)", Nullable: false, IsPrimaryKey: false) };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.True(report.HasDrift);
        var item = Assert.Single(report.Items);
        Assert.Equal(SchemaDriftKind.NullabilityChanged, item.Kind);
        Assert.Equal("Code", item.ColumnName);
    }

    // ── 6. same name but IsPrimaryKey changed → PrimaryKeyChanged ────────────

    [Fact]
    public void PrimaryKeyChanged_WhenPkFlips()
    {
        var snap = new[] { new ColumnInfo("Id", "NUMBER", Nullable: false, IsPrimaryKey: false) };
        var curr = new[] { new ColumnInfo("Id", "NUMBER", Nullable: false, IsPrimaryKey: true)  };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.True(report.HasDrift);
        var item = Assert.Single(report.Items);
        Assert.Equal(SchemaDriftKind.PrimaryKeyChanged, item.Kind);
    }

    // ── 7. case-insensitive name match → no spurious Added/Removed ───────────

    [Fact]
    public void CaseInsensitiveMatch_NoDriftWhenOnlyCasingDiffers()
    {
        var snap = new[] { Col("EmployeeId", "NUMBER") };
        var curr = new[] { Col("EMPLOYEEID", "NUMBER") };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.False(report.HasDrift);
    }

    // ── 8. type AND nullable both change → two separate items ────────────────

    [Fact]
    public void TwoItems_WhenTypeAndNullabilityBothChange()
    {
        var snap = new[] { new ColumnInfo("Score", "INTEGER",  Nullable: true,  IsPrimaryKey: false) };
        var curr = new[] { new ColumnInfo("Score", "FLOAT",    Nullable: false, IsPrimaryKey: false) };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.Equal(2, report.Items.Count);
        Assert.Contains(report.Items, i => i.Kind == SchemaDriftKind.TypeChanged);
        Assert.Contains(report.Items, i => i.Kind == SchemaDriftKind.NullabilityChanged);
    }

    // ── 9. empty snapshot + non-empty current → all Added ────────────────────

    [Fact]
    public void AllAdded_WhenSnapshotEmpty()
    {
        var curr = new[] { Col("A"), Col("B"), Col("C") };

        var report = SchemaDriftDetector.Compare([], curr);

        Assert.Equal(3, report.Items.Count);
        Assert.All(report.Items, i => Assert.Equal(SchemaDriftKind.Added, i.Kind));
    }

    // ── 10. non-empty snapshot + empty current → all Removed ─────────────────

    [Fact]
    public void AllRemoved_WhenCurrentEmpty()
    {
        var snap = new[] { Col("A"), Col("B") };

        var report = SchemaDriftDetector.Compare(snap, []);

        Assert.Equal(2, report.Items.Count);
        Assert.All(report.Items, i => Assert.Equal(SchemaDriftKind.Removed, i.Kind));
    }

    // ── 11. AffectingMappedColumns: Added does not count as affecting ─────────

    [Fact]
    public void AffectingMapped_AddedDoesNotCount()
    {
        // A is changed (TypeChanged) → mapped → affects
        // B is added (Added) → mapped → does NOT affect (new column, no existing mapping breaks)
        // C is changed (TypeChanged) → NOT mapped → doesn't affect via mapping count
        var snap = new[]
        {
            Col("A", "NUMBER"),
            Col("C", "VARCHAR2(50)"),
        };
        var curr = new[]
        {
            Col("A", "FLOAT"),     // TypeChanged — mapped
            Col("B"),              // Added       — mapped (but Added never affects)
            Col("C", "CHAR(10)"),  // TypeChanged — not mapped
        };

        var report = SchemaDriftDetector.Compare(snap, curr,
            mappedColumnNames: ["A", "B"]);

        Assert.Equal(1, report.AffectingMappedColumns);
    }

    // ── 12. Removed in mapped column → definitely affects ────────────────────

    [Fact]
    public void AffectingMapped_RemovedColumnCounts()
    {
        var snap = new[] { Col("ImportantCol", "NUMBER"), Col("Other", "NUMBER") };
        var curr = new[] { Col("Other", "NUMBER") };  // ImportantCol removed

        var report = SchemaDriftDetector.Compare(snap, curr,
            mappedColumnNames: ["ImportantCol"]);

        Assert.Equal(1, report.AffectingMappedColumns);
    }

    // ── 13. no mappedColumnNames provided → AffectingMappedColumns is null ───

    [Fact]
    public void AffectingMapped_NullWhenNoMappingProvided()
    {
        var snap = new[] { Col("X", "NUMBER") };
        var curr = new[] { Col("X", "VARCHAR2") };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.Null(report.AffectingMappedColumns);
    }

    // ── 14. ShortSummary lists grouped counts ─────────────────────────────────

    [Fact]
    public void ShortSummary_ListsGroupedKinds()
    {
        var snap = new[] { Col("A"), Col("B"), Col("C", "NUMBER") };
        var curr = new[] { Col("A"), Col("C", "FLOAT"), Col("D") };
        // B removed, C TypeChanged, D added

        var report = SchemaDriftDetector.Compare(snap, curr);

        var summary = report.ShortSummary();
        Assert.Contains("Removed", summary);
        Assert.Contains("TypeChanged", summary);
        Assert.Contains("Added", summary);
    }

    // ── 15. case-insensitive: type change still detected after name normalise ─

    [Fact]
    public void TypeChanged_DetectedEvenWithCaseDifferenceInName()
    {
        var snap = new[] { Col("employee_id", "INTEGER") };
        var curr = new[] { Col("EMPLOYEE_ID", "BIGINT") };

        var report = SchemaDriftDetector.Compare(snap, curr);

        Assert.True(report.HasDrift);
        Assert.Single(report.Items);
        Assert.Equal(SchemaDriftKind.TypeChanged, report.Items[0].Kind);
    }
}
