using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.IntegrationTests;

[Collection("E2E")]
public sealed class EtlEndToEndTests : IClassFixture<E2EFixture>
{
    private readonly E2EFixture _fx;
    public EtlEndToEndTests(E2EFixture fx) { _fx = fx; }

    private async Task<RunHistory> CreateAndRunAsync(EtlTask task)
    {
        using var scope = _fx.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<EtlTaskRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<EtlEngine>();
        var saved = await taskRepo.CreateAsync(task, default);
        var fullTask = (await taskRepo.GetWithMappingsAsync(saved.Id, default))!;
        return await engine.ExecuteAsync(fullTask, TriggerType.Manual, default);
    }

    [Fact]
    public async Task Oracle_to_MSSQL_DeleteInsert_with_form_filter()
    {
        await _fx.ResetTablesAsync();

        var task = new EtlTask
        {
            Name = $"E2E-OraToMssql-DI-Form-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR",
            SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo",
            TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = FilterTreeJson.Serialize(new FilterGroup
            {
                Logic = FilterLogic.And,
                Children =
                {
                    new FilterCondition { Column = "DEPARTMENT_ID", Operator = FilterOperator.In, Values = new() { "10", "20" } },
                },
            }),
            DeleteWhereSameAsFilter = true,
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
            },
        };

        var run = await CreateAndRunAsync(task);
        Assert.Equal(RunStatus.Success, run.Status);
        Assert.Equal(4, run.RowsRead);   // dept 10 (2 列) + dept 20 (2 列)
        Assert.Equal(4, run.RowsWritten);

        var tgt = await _fx.ReadMssqlTargetAsync();
        Assert.Equal(4, tgt.Count);
        Assert.All(tgt, r => Assert.True(r.dept == 10 || r.dept == 20));
        Assert.Contains(tgt, r => r.id == 1 && r.fn == "alice");
        // dept 30 (eve) 不應該被搬
        Assert.DoesNotContain(tgt, r => r.id == 5);
    }

    [Fact]
    public async Task Oracle_to_MSSQL_DeleteInsert_replays_overwrite()
    {
        await _fx.ResetTablesAsync();

        var task = new EtlTask
        {
            Name = $"E2E-OraToMssql-DI-Replay-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = FilterTreeJson.Serialize(new FilterGroup
            {
                Children =
                {
                    new FilterCondition { Column = "DEPARTMENT_ID", Operator = FilterOperator.Eq, Value = "10" },
                },
            }),
            DeleteWhereSameAsFilter = true,
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
            },
        };
        var r1 = await CreateAndRunAsync(task);
        Assert.Equal(RunStatus.Success, r1.Status);
        Assert.Equal(2, r1.RowsWritten);

        // 改 source、再跑：應該替換掉同條件的舊資料
        await _fx.UpdateOracleSrcSalaryAsync(1, 99999m);

        // 再跑一次（不再透過 task repo create，直接 trigger 之前那個）
        using (var scope = _fx.Services.CreateScope())
        {
            var taskRepo = scope.ServiceProvider.GetRequiredService<EtlTaskRepository>();
            var engine = scope.ServiceProvider.GetRequiredService<EtlEngine>();
            var all = await taskRepo.ListLightweightAsync(default);
            var t = await taskRepo.GetWithMappingsAsync(all.First(x => x.Name == task.Name).Id, default);
            var r2 = await engine.ExecuteAsync(t!, TriggerType.Manual, default);
            Assert.Equal(RunStatus.Success, r2.Status);
            Assert.Equal(2, r2.RowsWritten);
        }

        var tgt = await _fx.ReadMssqlTargetAsync();
        Assert.Equal(2, tgt.Count);
        Assert.All(tgt, r => Assert.Equal(10, r.dept));
    }

    [Fact]
    public async Task Oracle_to_MSSQL_Upsert_updates_existing_and_inserts_new()
    {
        await _fx.ResetTablesAsync();

        var task = new EtlTask
        {
            Name = $"E2E-OraToMssql-Upsert-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.Upsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = null,  // 全表
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
                new ColumnMapping { SourceColumn = "SALARY", TargetColumn = "SALARY", OrderIndex = 4 },
                new ColumnMapping { SourceColumn = "HIRE_DATE", TargetColumn = "HIRE_DATE", OrderIndex = 5 },
            },
        };
        var r1 = await CreateAndRunAsync(task);
        Assert.Equal(RunStatus.Success, r1.Status);
        Assert.Equal(5, r1.RowsRead);

        // 改 source（id=1 改名）+ 加新 (id=999)
        var ora = _fx.CreateOracle();
        await using (var c = await ora.OpenAsync(default))
        {
            await using var u = c.CreateCommand();
            u.CommandText = "UPDATE HR.EMPLOYEES_SRC SET LAST_NAME = 'AndersonV2' WHERE EMPLOYEE_ID = 1";
            await u.ExecuteNonQueryAsync();
        }
        await _fx.InsertOracleSrcAsync(999, "zoe", "Zhao", 99, 12345m);

        // 再跑
        using (var scope = _fx.Services.CreateScope())
        {
            var taskRepo = scope.ServiceProvider.GetRequiredService<EtlTaskRepository>();
            var engine = scope.ServiceProvider.GetRequiredService<EtlEngine>();
            var all = await taskRepo.ListLightweightAsync(default);
            var t = await taskRepo.GetWithMappingsAsync(all.First(x => x.Name == task.Name).Id, default);
            var r2 = await engine.ExecuteAsync(t!, TriggerType.Manual, default);
            Assert.Equal(RunStatus.Success, r2.Status);
        }

        var tgt = await _fx.ReadMssqlTargetAsync();
        Assert.Equal(6, tgt.Count); // 5 原本 + 1 新 (zoe)
        Assert.Contains(tgt, r => r.id == 1 && r.ln == "AndersonV2"); // 已更新
        Assert.Contains(tgt, r => r.id == 999 && r.fn == "zoe"); // 已插入
    }

    [Fact]
    public async Task MSSQL_to_Oracle_DeleteInsert_reverse_direction()
    {
        await _fx.ResetTablesAsync();

        var task = new EtlTask
        {
            Name = $"E2E-MssqlToOra-DI-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.MssqlConnId,
            SourceSchema = "dbo", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.OracleConnId,
            TargetSchema = "HR", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = FilterTreeJson.Serialize(new FilterGroup
            {
                Children =
                {
                    new FilterCondition { Column = "DEPARTMENT_ID", Operator = FilterOperator.Eq, Value = "40" },
                },
            }),
            DeleteWhereSameAsFilter = true,
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
            },
        };
        var run = await CreateAndRunAsync(task);
        if (run.Status != RunStatus.Success)
            throw new Xunit.Sdk.XunitException($"Run failed: {run.ErrorMessage}\nReadSql: {run.GeneratedReadSql}\nWriteSql: {run.GeneratedWriteSql}");
        Assert.Equal(2, run.RowsWritten);  // dept 40：frank + grace

        var tgt = await _fx.ReadOracleTargetAsync();
        Assert.Equal(2, tgt.Count);
        Assert.All(tgt, r => Assert.Equal(40, r.dept));
        Assert.Contains(tgt, r => r.id == 101 && r.fn == "frank");
        Assert.Contains(tgt, r => r.id == 102 && r.fn == "grace");
    }

    [Fact]
    public async Task Transform_expression_uppercases_value()
    {
        await _fx.ResetTablesAsync();

        var task = new EtlTask
        {
            Name = $"E2E-Transform-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = FilterTreeJson.Serialize(new FilterGroup
            {
                Children =
                {
                    new FilterCondition { Column = "EMPLOYEE_ID", Operator = FilterOperator.Eq, Value = "1" },
                },
            }),
            DeleteWhereSameAsFilter = true,
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1,
                    TransformExpression = "row[\"FIRST_NAME\"].ToString().ToUpper()" },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
            },
        };
        var run = await CreateAndRunAsync(task);
        Assert.Equal(RunStatus.Success, run.Status);

        var tgt = await _fx.ReadMssqlTargetAsync();
        Assert.Single(tgt);
        Assert.Equal("ALICE", tgt[0].fn);   // 原本是 "alice"，經 ToUpper 後變成 "ALICE"
    }

    [Fact]
    public async Task Raw_sql_filter_mode()
    {
        await _fx.ResetTablesAsync();

        var task = new EtlTask
        {
            Name = $"E2E-RawSql-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.RawSql,
            FilterRawSql = "SALARY >= 60000",
            DeleteWhereSameAsFilter = false,
            DeleteWhereRawSql = "1=1",   // 整表清掉
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
            },
        };
        var run = await CreateAndRunAsync(task);
        Assert.Equal(RunStatus.Success, run.Status);
        // SALARY >= 60000：carol(60000), dave(62000), eve(70000) = 3 列
        Assert.Equal(3, run.RowsWritten);

        var tgt = await _fx.ReadMssqlTargetAsync();
        Assert.Equal(3, tgt.Count);
        Assert.Contains(tgt, r => r.id == 3);
        Assert.Contains(tgt, r => r.id == 4);
        Assert.Contains(tgt, r => r.id == 5);
    }

    [Fact]
    public async Task Failure_rolls_back_target_unchanged()
    {
        await _fx.ResetTablesAsync();

        // 先用 DeleteInsert 寫入 dept 10 的兩筆作為「既有資料」
        var seedTask = new EtlTask
        {
            Name = $"E2E-Rollback-Seed-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = FilterTreeJson.Serialize(new FilterGroup
            {
                Children = { new FilterCondition { Column = "DEPARTMENT_ID", Operator = FilterOperator.Eq, Value = "10" } },
            }),
            DeleteWhereSameAsFilter = true,
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0 },
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
            },
        };
        var seedRun = await CreateAndRunAsync(seedTask);
        Assert.Equal(RunStatus.Success, seedRun.Status);
        var beforeTgt = await _fx.ReadMssqlTargetAsync();
        Assert.Equal(2, beforeTgt.Count);

        // 故意做一個會寫失敗的 task：把 SALARY 對到 NVARCHAR FIRST_NAME（型別衝突），
        // 並使用 dept 10 的條件，預期：先 DELETE 兩筆 dept 10 → 嘗試 INSERT 失敗 → rollback → target 仍是先前 2 筆
        // 簡單做法：把 EMPLOYEE_ID（PK NOT NULL）映射為 null 來源 — 用 transform 強制為 null
        var failTask = new EtlTask
        {
            Name = $"E2E-Rollback-Fail-{Guid.NewGuid():N}",
            SourceConnectionId = _fx.OracleConnId,
            SourceSchema = "HR", SourceTable = "EMPLOYEES_SRC",
            TargetConnectionId = _fx.MssqlConnId,
            TargetSchema = "dbo", TargetTable = "EMPLOYEES_TGT",
            WriteMode = WriteMode.DeleteInsert,
            FilterMode = FilterMode.FormBuilder,
            FilterFormJson = FilterTreeJson.Serialize(new FilterGroup
            {
                Children = { new FilterCondition { Column = "DEPARTMENT_ID", Operator = FilterOperator.Eq, Value = "10" } },
            }),
            DeleteWhereSameAsFilter = true,
            BatchSize = 100,
            CronExpression = "0 0 * * * ?",
            Mappings =
            {
                new ColumnMapping { SourceColumn = "EMPLOYEE_ID", TargetColumn = "EMPLOYEE_ID", IsKey = true, OrderIndex = 0,
                    TransformExpression = "(object)null" },   // 故意產生 null PK
                new ColumnMapping { SourceColumn = "FIRST_NAME", TargetColumn = "FIRST_NAME", OrderIndex = 1 },
                new ColumnMapping { SourceColumn = "LAST_NAME", TargetColumn = "LAST_NAME", OrderIndex = 2 },
                new ColumnMapping { SourceColumn = "DEPARTMENT_ID", TargetColumn = "DEPARTMENT_ID", OrderIndex = 3 },
            },
        };

        var failRun = await CreateAndRunAsync(failTask);
        Assert.Equal(RunStatus.Failed, failRun.Status);
        Assert.False(string.IsNullOrEmpty(failRun.ErrorMessage));

        // target 應仍維持先前的 2 筆（rollback 生效）
        var afterTgt = await _fx.ReadMssqlTargetAsync();
        Assert.Equal(2, afterTgt.Count);
        Assert.Equal(beforeTgt.Select(r => r.id).OrderBy(x => x),
                     afterTgt.Select(r => r.id).OrderBy(x => x));
    }

    [Fact]
    public async Task Test_connection_works_for_both()
    {
        var ora = _fx.CreateOracle();
        var sql = _fx.CreateMssql();
        Assert.True(await ora.TestConnectionAsync(default));
        Assert.True(await sql.TestConnectionAsync(default));
    }

    [Fact]
    public async Task List_schemas_tables_columns_for_both()
    {
        var ora = _fx.CreateOracle();
        var sql = _fx.CreateMssql();

        var oraSchemas = await ora.ListSchemasAsync(default);
        Assert.Contains("HR", oraSchemas);
        var oraTables = await ora.ListTablesAsync("HR", default);
        Assert.Contains("EMPLOYEES_SRC", oraTables);
        var oraCols = await ora.ListColumnsAsync("HR", "EMPLOYEES_SRC", default);
        Assert.Contains(oraCols, c => c.Name == "EMPLOYEE_ID" && c.IsPrimaryKey);

        var sqlSchemas = await sql.ListSchemasAsync(default);
        Assert.Contains("dbo", sqlSchemas);
        var sqlTables = await sql.ListTablesAsync("dbo", default);
        Assert.Contains("EMPLOYEES_SRC", sqlTables);
        var sqlCols = await sql.ListColumnsAsync("dbo", "EMPLOYEES_SRC", default);
        Assert.Contains(sqlCols, c => c.Name == "EMPLOYEE_ID" && c.IsPrimaryKey);
    }
}
