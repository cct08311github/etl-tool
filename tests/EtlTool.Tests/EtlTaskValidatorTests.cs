using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class EtlTaskValidatorTests
{
    private static EtlTask Valid()
    {
        var srcConn = Guid.NewGuid();
        var tgtConn = Guid.NewGuid();
        return new EtlTask
        {
            Name = "T",
            SourceConnectionId = srcConn,
            SourceSchema = "HR",
            SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = tgtConn,
            TargetSchema = "dbo",
            TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            BatchSize = 1000,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "ID", TargetColumn = "ID", IsKey = true },
                new ColumnMapping { SourceColumn = "NAME", TargetColumn = "NAME" },
            },
        };
    }

    [Fact]
    public void Valid_task_has_no_errors() =>
        Assert.Empty(EtlTaskValidator.Validate(Valid()));

    [Fact]
    public void Empty_name_flagged()
    {
        var t = Valid(); t.Name = "";
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("名稱"));
    }

    [Fact]
    public void Same_source_and_target_table_flagged()
    {
        var t = Valid();
        t.TargetConnectionId = t.SourceConnectionId;
        t.TargetSchema = t.SourceSchema;
        t.TargetTable = t.SourceTable;
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("同一張表"));
    }

    [Fact]
    public void No_mappings_flagged()
    {
        var t = Valid(); t.Mappings.Clear();
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("映射"));
    }

    [Fact]
    public void Mapping_with_empty_field_flagged()
    {
        var t = Valid(); t.Mappings[0].SourceColumn = "";
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("空"));
    }

    [Fact]
    public void Duplicate_target_column_flagged()
    {
        var t = Valid();
        t.Mappings.Add(new ColumnMapping { SourceColumn = "X", TargetColumn = "ID" });
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("重複"));
    }

    [Fact]
    public void Upsert_without_key_flagged()
    {
        var t = Valid();
        t.WriteMode = WriteMode.Upsert;
        foreach (var m in t.Mappings) m.IsKey = false;
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("主鍵"));
    }

    [Fact]
    public void Bad_cron_flagged()
    {
        var t = Valid(); t.CronExpression = "this is not cron";
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("Cron"));
    }

    [Fact]
    public void Bad_batch_size_flagged()
    {
        var t = Valid(); t.BatchSize = 0;
        Assert.Contains(EtlTaskValidator.Validate(t), e => e.Contains("批次"));
    }
}
