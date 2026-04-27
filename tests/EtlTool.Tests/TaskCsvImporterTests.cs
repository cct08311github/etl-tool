using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class TaskCsvImporterTests
{
    private const string Header = "name,source_connection,source_schema,source_table," +
        "target_connection,target_schema,target_table,write_mode,cron,enabled,tags";

    [Fact]
    public void Empty_input_returns_empty()
    {
        var result = TaskCsvImporter.Parse("");
        Assert.Equal(0, result.OkCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Missing_required_header_reports_error()
    {
        var result = TaskCsvImporter.Parse("name,source_connection\n");
        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("Missing", result.Rows[0].Error!);
    }

    [Fact]
    public void Single_valid_row_parses()
    {
        var csv = Header + "\n" +
            "MyTask,prod-mssql,dbo,Orders_SRC,prod-oracle,HR,Orders_TGT,DeleteInsert,0 0 2 * * ?,true,\"daily,critical\"";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(1, result.OkCount);
        Assert.Equal(0, result.ErrorCount);
        var row = result.Rows[0];
        Assert.True(row.Ok);
        Assert.Equal("MyTask", row.Name);
        Assert.Equal("prod-mssql", row.SourceConnection);
        Assert.Equal(WriteMode.DeleteInsert, row.WriteMode);
        Assert.True(row.Enabled);
        Assert.Equal("daily,critical", row.Tags);
    }

    [Fact]
    public void Empty_required_field_reports_error()
    {
        // Empty name
        var csv = Header + "\n" +
            ",src,sch,tbl,tgt,sch,tbl,DeleteInsert,0 0 2 * * ?,true,";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(0, result.OkCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("name is empty", result.Rows[0].Error!);
    }

    [Fact]
    public void Multiple_errors_in_row_concatenated()
    {
        var csv = Header + "\n" +
            ",src,,,tgt,,,InvalidMode,not a cron,maybe,";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(1, result.ErrorCount);
        var err = result.Rows[0].Error!;
        Assert.Contains("name is empty", err);
        Assert.Contains("source_table is empty", err);
        Assert.Contains("target_table is empty", err);
        Assert.Contains("write_mode", err);
        Assert.Contains("cron invalid", err);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", true)] // default
    [InlineData("garbage", true)] // unknown → default
    public void Enabled_field_parses_various_truthy_falsy(string input, bool expected)
    {
        var csv = Header + "\n" +
            $"T,src,s,t,tgt,s,t,DeleteInsert,0 0 2 * * ?,{input},";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(expected, result.Rows[0].Enabled);
    }

    [Fact]
    public void Quoted_field_with_comma_handled_correctly()
    {
        var csv = Header + "\n" +
            "\"My, Task\",src,s,t,tgt,s,t,Upsert,0 0 2 * * ?,true,\"daily,critical\"";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(1, result.OkCount);
        Assert.Equal("My, Task", result.Rows[0].Name);
        Assert.Equal("daily,critical", result.Rows[0].Tags);
    }

    [Fact]
    public void Doubled_quotes_inside_quoted_field_unescaped()
    {
        var csv = Header + "\n" +
            "\"My \"\"Quoted\"\" Task\",src,s,t,tgt,s,t,Upsert,0 0 2 * * ?,true,";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal("My \"Quoted\" Task", result.Rows[0].Name);
    }

    [Fact]
    public void Multiple_rows_all_valid()
    {
        var csv = Header + "\n" +
            "T1,src,s,t1,tgt,s,t1,DeleteInsert,0 0 2 * * ?,true,\n" +
            "T2,src,s,t2,tgt,s,t2,Upsert,0 0 3 * * ?,false,daily";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(2, result.OkCount);
        Assert.Equal("T1", result.Rows[0].Name);
        Assert.Equal("T2", result.Rows[1].Name);
        Assert.True(result.Rows[0].Enabled);
        Assert.False(result.Rows[1].Enabled);
    }

    [Fact]
    public void Mixed_valid_and_invalid_reports_both()
    {
        var csv = Header + "\n" +
            "T1,src,s,t1,tgt,s,t1,DeleteInsert,0 0 2 * * ?,true,\n" +
            ",src,s,t2,tgt,s,t2,Upsert,0 0 3 * * ?,false,";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(1, result.OkCount);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void Trailing_blank_lines_ignored()
    {
        var csv = Header + "\n" +
            "T,src,s,t,tgt,s,t,Upsert,0 0 2 * * ?,true,\n\n\n";
        var result = TaskCsvImporter.Parse(csv);
        Assert.Equal(1, result.OkCount);
    }

    [Fact]
    public void ParseCsvLine_basic()
    {
        Assert.Equal(new[] { "a", "b", "c" }, TaskCsvImporter.ParseCsvLine("a,b,c"));
    }

    [Fact]
    public void ParseCsvLine_empty_fields()
    {
        Assert.Equal(new[] { "", "", "" }, TaskCsvImporter.ParseCsvLine(",,"));
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("she said \"hi\"", "\"she said \"\"hi\"\"\"")]
    [InlineData("multi\nline", "\"multi\nline\"")]
    public void CsvEscape_quotes_when_needed(string input, string expected)
    {
        Assert.Equal(expected, TaskCsvImporter.CsvEscape(input));
    }

    [Fact]
    public void CanonicalHeader_exposes_expected_columns()
    {
        var header = TaskCsvImporter.CanonicalHeader;
        Assert.Contains("name", header);
        Assert.Contains("source_connection", header);
        Assert.Contains("write_mode", header);
        Assert.Contains("tags", header);
    }

    [Fact]
    public void Render_then_parse_round_trips()
    {
        var srcConnId = Guid.NewGuid();
        var tgtConnId = Guid.NewGuid();
        var task = new EtlTool.Core.Models.EtlTask
        {
            Name = "RoundTrip,Test",  // commas in name require escape
            SourceConnectionId = srcConnId,
            SourceSchema = "dbo",
            SourceTable = "T1",
            TargetConnectionId = tgtConnId,
            TargetSchema = "HR",
            TargetTable = "T2",
            WriteMode = WriteMode.Upsert,
            CronExpression = "0 0 2 * * ?",
            Enabled = false,
            Tags = "daily,critical",
        };
        var connNames = new Dictionary<Guid, string>
        {
            [srcConnId] = "src-conn",
            [tgtConnId] = "tgt-conn",
        };

        var csv = TaskCsvImporter.Render(new[] { task }, connNames);
        var result = TaskCsvImporter.Parse(csv);

        Assert.Equal(1, result.OkCount);
        var row = result.Rows[0];
        Assert.Equal("RoundTrip,Test", row.Name);
        Assert.Equal("src-conn", row.SourceConnection);
        Assert.Equal("tgt-conn", row.TargetConnection);
        Assert.Equal(WriteMode.Upsert, row.WriteMode);
        Assert.False(row.Enabled);
        Assert.Equal("daily,critical", row.Tags);
    }

    [Fact]
    public void Render_uses_GUID_when_connection_name_missing()
    {
        var srcConnId = Guid.NewGuid();
        var tgtConnId = Guid.NewGuid();
        var task = new EtlTool.Core.Models.EtlTask
        {
            Name = "T",
            SourceConnectionId = srcConnId,
            TargetConnectionId = tgtConnId,
            SourceTable = "x",
            TargetTable = "y",
            CronExpression = "0 0 * * * ?",
        };
        // empty lookup
        var csv = TaskCsvImporter.Render(new[] { task },
            new Dictionary<Guid, string>());
        Assert.Contains(srcConnId.ToString(), csv);
        Assert.Contains(tgtConnId.ToString(), csv);
    }
}
