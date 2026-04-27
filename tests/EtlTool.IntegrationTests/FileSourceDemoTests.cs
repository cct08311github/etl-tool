using ClosedXML.Excel;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.IntegrationTests;

/// <summary>
/// End-to-end demo: 4 種檔案格式 → MSSQL dbo.FileImportDemo 表。
///
/// 跑前置條件：
///   1. docker-compose dev DBs 已啟動（etltool-mssql up）
///   2. EtlTest DB 內有空表 dbo.FileImportDemo（手動建好或由 user setup 建）
///
/// 每個測試：
///   1. 在 /tmp/etltool-demo 內準備一個對應格式的檔案
///   2. 構造 in-memory EtlTask（SourceKind.File）指向該檔
///   3. 呼叫 EtlEngine.ExecuteAsync
///   4. 驗證 RunHistory.Status = Success + RowsRead/Written 正確
///   5. 用獨立 query 從 MSSQL 撈資料確認 row 內容（不靠 RunHistory，做真實 round-trip）
/// </summary>
public class FileSourceDemoTests : IClassFixture<E2EFixture>, IAsyncLifetime
{
    private readonly E2EFixture _fx;
    private readonly string _demoDir = "/tmp/etltool-demo";

    public FileSourceDemoTests(E2EFixture fx) { _fx = fx; }

    public async Task InitializeAsync()
    {
        // 清空 target 表（每個測試獨立）
        var sql = _fx.CreateMssql();
        await using var c = await sql.OpenAsync(default);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.FileImportDemo";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Csv_to_mssql_imports_3_rows()
    {
        // 假設 user 已建好 customers.csv（或我們自己生）
        var csvPath = Path.Combine(_demoDir, "customers.csv");
        Assert.True(File.Exists(csvPath), $"Demo file missing: {csvPath}");

        var task = MakeFileTask("CSV demo", new FileSourceConfig
        {
            DirectoryPath = _demoDir,
            GlobPattern = "customers.csv",
            Format = FileSourceFormat.Csv,
            Encoding = "utf-8",
            HasHeader = true,
            Delimiter = ",",
            QuoteCharacter = "\"",
            PostAction = FilePostAction.None,  // demo 用不做歸檔，測完原檔留著
            MaxFilesPerRun = 1,
        }, sourceFormatLabel: "csv");

        await RunAndAssert(task, expectedRows: 3, expectedFirstId: 1001);
    }

    [Fact]
    public async Task Excel_to_mssql_imports_3_rows()
    {
        // 動態產生 Excel demo 檔
        var xlsxPath = Path.Combine(_demoDir, "customers.xlsx");
        WriteExcelDemo(xlsxPath);

        var task = MakeFileTask("Excel demo", new FileSourceConfig
        {
            DirectoryPath = _demoDir,
            GlobPattern = "customers.xlsx",
            Format = FileSourceFormat.Excel,
            HasHeader = true,
            ExcelSheetName = "",  // 第一個 sheet
            PostAction = FilePostAction.None,
            MaxFilesPerRun = 1,
        }, sourceFormatLabel: "excel");

        await RunAndAssert(task, expectedRows: 3, expectedFirstId: 4001);
    }

    [Fact]
    public async Task Tsv_to_mssql_imports_2_rows()
    {
        var tsvPath = Path.Combine(_demoDir, "orders.tsv");
        Assert.True(File.Exists(tsvPath), $"Demo file missing: {tsvPath}");

        var task = MakeFileTask("TSV demo", new FileSourceConfig
        {
            DirectoryPath = _demoDir,
            GlobPattern = "orders.tsv",
            Format = FileSourceFormat.DelimitedText,
            Encoding = "utf-8",
            HasHeader = true,
            Delimiter = "\\t",  // 文字面 \t 在 reader 內 unescape
            PostAction = FilePostAction.None,
            MaxFilesPerRun = 1,
        }, sourceFormatLabel: "tsv");

        await RunAndAssert(task, expectedRows: 2, expectedFirstId: 2001);
    }

    [Fact]
    public async Task FixedWidth_to_mssql_imports_3_rows()
    {
        var path = Path.Combine(_demoDir, "journal.txt");
        Assert.True(File.Exists(path), $"Demo file missing: {path}");

        // Layout 對照 journal.txt 內容（純 ASCII）：
        //   id    pos 1-5   (5)
        //   name  pos 6-25  (20)
        //   region pos 26-35 (10)
        //   amount pos 36-43 (8)
        var layout = new List<FixedWidthColumn>
        {
            new() { Name = "id", Start = 1, Length = 5, TrimWhitespace = true },
            new() { Name = "name", Start = 6, Length = 20, TrimWhitespace = true },
            new() { Name = "region", Start = 26, Length = 10, TrimWhitespace = true },
            new() { Name = "amount", Start = 36, Length = 8, TrimWhitespace = true },
        };
        var task = MakeFileTask("FixedWidth demo", new FileSourceConfig
        {
            DirectoryPath = _demoDir,
            GlobPattern = "journal.txt",
            Format = FileSourceFormat.FixedWidth,
            Encoding = "utf-8",
            FixedWidthLayoutJson = System.Text.Json.JsonSerializer.Serialize(layout),
            PostAction = FilePostAction.None,
            MaxFilesPerRun = 1,
        }, sourceFormatLabel: "fixed");

        await RunAndAssert(task, expectedRows: 3, expectedFirstId: 3001);
    }

    // ── 共用 helpers ────────────────────────────────────────────────

    private EtlTask MakeFileTask(string name, FileSourceConfig fileConfig, string sourceFormatLabel)
    {
        var task = new EtlTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            Enabled = true,
            SourceKind = SourceKind.File,
            FileSourceConfigJson = System.Text.Json.JsonSerializer.Serialize(fileConfig),
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo",
            TargetTable = "FileImportDemo",
            WriteMode = WriteMode.DeleteInsert,
            BatchSize = 100,
            CronExpression = "0 0 0 * * ?",  // 不會自己觸發；我們手動跑
            DeleteWhereSameAsFilter = false,  // 我們沒設 filter，刪全表
            DeleteWhereRawSql = "1=1",
        };
        // 4 個欄位 + 一個常數欄位 SourceFormat（用 transform 帶入）
        task.Mappings.AddRange(new[]
        {
            new ColumnMapping { SourceColumn = "id", TargetColumn = "Id" },
            new ColumnMapping { SourceColumn = "name", TargetColumn = "Name" },
            new ColumnMapping { SourceColumn = "region", TargetColumn = "Region" },
            new ColumnMapping { SourceColumn = "amount", TargetColumn = "Amount" },
            new ColumnMapping
            {
                SourceColumn = "id",  // 任一存在的 source 欄都行；transform 不依賴它
                TargetColumn = "SourceFormat",
                TransformExpression = $"\"{sourceFormatLabel}\"",
            },
        });
        return task;
    }

    private async Task RunAndAssert(EtlTask task, int expectedRows, int expectedFirstId)
    {
        // 執行 ETL
        using var scope = _fx.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<EtlEngine>();
        var run = await engine.ExecuteAsync(task, TriggerType.Manual, CancellationToken.None);

        // RunHistory 結果
        if (run.Status == RunStatus.Failed)
        {
            // 失敗時印出細節方便診斷
            throw new Xunit.Sdk.XunitException(
                $"Run failed: {run.ErrorMessage}\n" +
                $"GeneratedReadSql:\n{run.GeneratedReadSql}\n" +
                $"GeneratedWriteSql:\n{run.GeneratedWriteSql}");
        }
        Assert.Equal(RunStatus.Success, run.Status);
        Assert.Equal(expectedRows, run.RowsRead);
        Assert.Equal(expectedRows, run.RowsWritten);

        // MSSQL 端 round-trip 驗證 — 不依賴 RunHistory，獨立 query 確認 row 進去了
        var sql = _fx.CreateMssql();
        await using var c = await sql.OpenAsync(default);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.FileImportDemo";
        var count = (int)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(expectedRows, count);

        await using var firstIdCmd = c.CreateCommand();
        firstIdCmd.CommandText = "SELECT MIN(Id) FROM dbo.FileImportDemo";
        var minId = (int)(await firstIdCmd.ExecuteScalarAsync())!;
        Assert.Equal(expectedFirstId, minId);
    }

    private static void WriteExcelDemo(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Customers");
        ws.Cell(1, 1).Value = "id";
        ws.Cell(1, 2).Value = "name";
        ws.Cell(1, 3).Value = "region";
        ws.Cell(1, 4).Value = "amount";
        ws.Cell(2, 1).Value = 4001;
        ws.Cell(2, 2).Value = "Iris Lin";
        ws.Cell(2, 3).Value = "新竹市";
        ws.Cell(2, 4).Value = 11200.00;
        ws.Cell(3, 1).Value = 4002;
        ws.Cell(3, 2).Value = "Jack Liu";
        ws.Cell(3, 3).Value = "宜蘭縣";
        ws.Cell(3, 4).Value = 8500.50;
        ws.Cell(4, 1).Value = 4003;
        ws.Cell(4, 2).Value = "Karen Su";
        ws.Cell(4, 3).Value = "花蓮縣";
        ws.Cell(4, 4).Value = 17800.00;
        wb.SaveAs(path);
    }
}
