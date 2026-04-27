using EtlTool.Connectors;
using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlTool.IntegrationTests;

/// <summary>
/// E2E 測試 fixture：
/// - 起一個 in-memory（檔案系統）SQLite 應用 DB
/// - 註冊與 Program.cs 同樣的服務（除了 Blazor）
/// - 提供 Oracle、MSSQL 的連線工具方法（重設表、查表等）
/// 在 docker-compose.dev.yml 起來的條件下執行。
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public const string OracleConnString = "User Id=system;Password=oracle;Data Source=localhost:1521/XEPDB1";
    public const string MssqlConnString = "Server=localhost,1433;Database=EtlTest;User Id=sa;Password=Dev_Password1!;TrustServerCertificate=true;Encrypt=false";

    private string _dbPath = "";
    public ServiceProvider Services { get; private set; } = null!;
    public Guid OracleConnId { get; private set; }
    public Guid MssqlConnId { get; private set; }

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"etltool-e2e-{Guid.NewGuid():N}.db");
        var keysDir = Path.Combine(Path.GetTempPath(), $"etltool-e2e-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDir);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName("EtlTool.E2E");

        services.AddDbContext<AppDbContext>(opt => opt.UseSqlite($"Data Source={_dbPath}"));

        services.AddScoped<IConnectionStringProtector, DataProtectionConnectionStringProtector>();
        services.AddSingleton<EtlTool.Core.Engine.IAuditLogger, AuditLogger>();
        services.AddScoped<EntityChangeHistoryRepository>();
        services.AddScoped<ConnectionRepository>();
        services.AddScoped<EtlTaskRepository>();
        services.AddScoped<RunHistoryRepository>();

        services.AddScoped<IConnectionLookup>(sp => sp.GetRequiredService<ConnectionRepository>());
        services.AddScoped<IRunHistorySink>(sp => sp.GetRequiredService<RunHistoryRepository>());

        services.AddScoped<IDbConnectorFactory, DbConnectorFactory>();
        services.AddScoped<EtlEngine>();

        Services = services.BuildServiceProvider();

        // Migrate
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        // 建立兩條連線
        using (var scope = Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ConnectionRepository>();
            var protector = scope.ServiceProvider.GetRequiredService<IConnectionStringProtector>();
            var ora = await repo.CreateAsync("Test Oracle", DbProviderType.Oracle, OracleConnString, protector, default);
            var sql = await repo.CreateAsync("Test SqlServer", DbProviderType.SqlServer, MssqlConnString, protector, default);
            OracleConnId = ora.Id;
            MssqlConnId = sql.Id;
        }
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
        {
            await Services.DisposeAsync();
        }
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    public IDbConnector CreateOracle() => new EtlTool.Connectors.Oracle.OracleConnector(OracleConnString);
    public IDbConnector CreateMssql() => new EtlTool.Connectors.SqlServer.SqlServerConnector(MssqlConnString);

    /// <summary>把所有測試表清空（保留 schema）並依模式重置初始資料。</summary>
    public async Task ResetTablesAsync()
    {
        // Oracle SRC: 5 列 dept 10/20/30，TGT: 空
        var ora = CreateOracle();
        await using var oc = await ora.OpenAsync(default);
        await using (var c1 = oc.CreateCommand())
        {
            c1.CommandText = "DELETE FROM HR.EMPLOYEES_TGT";
            await c1.ExecuteNonQueryAsync();
        }
        await using (var c2 = oc.CreateCommand())
        {
            c2.CommandText = "DELETE FROM HR.EMPLOYEES_SRC";
            await c2.ExecuteNonQueryAsync();
        }
        var seedRows = new (int id, string fn, string ln, int dept, decimal sal, DateTime hire)[]
        {
            (1, "alice", "Anderson", 10, 50000m, new DateTime(2024,1,15)),
            (2, "bob",   "Brown",    10, 55000m, new DateTime(2024,2,20)),
            (3, "carol", "Chen",     20, 60000m, new DateTime(2024,3,10)),
            (4, "dave",  "Davis",    20, 62000m, new DateTime(2024,4,5)),
            (5, "eve",   "Evans",    30, 70000m, new DateTime(2024,5,12)),
        };
        foreach (var r in seedRows)
        {
            await using var ins = oc.CreateCommand();
            ins.CommandText = "INSERT INTO HR.EMPLOYEES_SRC (EMPLOYEE_ID, FIRST_NAME, LAST_NAME, DEPARTMENT_ID, SALARY, HIRE_DATE) VALUES (:i, :f, :l, :d, :s, :h)";
            ins.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("i", r.id));
            ins.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("f", r.fn));
            ins.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("l", r.ln));
            ins.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("d", r.dept));
            ins.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("s", r.sal));
            ins.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("h", r.hire));
            await ins.ExecuteNonQueryAsync();
        }

        // MSSQL SRC: 5 列 dept 40/50/60，TGT: 空
        var sql = CreateMssql();
        await using var sc = await sql.OpenAsync(default);
        await using (var c3 = sc.CreateCommand())
        {
            c3.CommandText = "DELETE FROM dbo.EMPLOYEES_TGT; DELETE FROM dbo.EMPLOYEES_SRC;";
            await c3.ExecuteNonQueryAsync();
        }
        var sqlSeed = new (int id, string fn, string ln, int dept, decimal sal, DateTime hire)[]
        {
            (101, "frank", "Foster", 40, 80000m, new DateTime(2024,6,1)),
            (102, "grace", "Garcia", 40, 82000m, new DateTime(2024,6,15)),
            (103, "henry", "Huang",  50, 90000m, new DateTime(2024,7,1)),
            (104, "irene", "Ito",    50, 95000m, new DateTime(2024,7,20)),
            (105, "jack",  "Jones",  60, 75000m, new DateTime(2024,8,10)),
        };
        foreach (var r in sqlSeed)
        {
            await using var ins = sc.CreateCommand();
            ins.CommandText = "INSERT INTO dbo.EMPLOYEES_SRC (EMPLOYEE_ID, FIRST_NAME, LAST_NAME, DEPARTMENT_ID, SALARY, HIRE_DATE) VALUES (@i, @f, @l, @d, @s, @h)";
            ins.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@i", r.id));
            ins.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@f", r.fn));
            ins.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@l", r.ln));
            ins.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@d", r.dept));
            ins.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@s", r.sal));
            ins.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@h", r.hire));
            await ins.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<(int id, string fn, string ln, int? dept)>> ReadOracleTargetAsync()
    {
        var ora = CreateOracle();
        await using var oc = await ora.OpenAsync(default);
        await using var cmd = oc.CreateCommand();
        cmd.CommandText = "SELECT EMPLOYEE_ID, FIRST_NAME, LAST_NAME, DEPARTMENT_ID FROM HR.EMPLOYEES_TGT ORDER BY EMPLOYEE_ID";
        var list = new List<(int, string, string, int?)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((
                Convert.ToInt32(r.GetValue(0)),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r.GetValue(3))));
        }
        return list;
    }

    public async Task<List<(int id, string fn, string ln, int? dept)>> ReadMssqlTargetAsync()
    {
        var sql = CreateMssql();
        await using var sc = await sql.OpenAsync(default);
        await using var cmd = sc.CreateCommand();
        cmd.CommandText = "SELECT EMPLOYEE_ID, FIRST_NAME, LAST_NAME, DEPARTMENT_ID FROM dbo.EMPLOYEES_TGT ORDER BY EMPLOYEE_ID";
        var list = new List<(int, string, string, int?)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((
                r.GetInt32(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? (int?)null : r.GetInt32(3)));
        }
        return list;
    }

    /// <summary>更新 Oracle SRC 的某一列（用於 Upsert 變更測試）</summary>
    public async Task UpdateOracleSrcSalaryAsync(int id, decimal newSalary)
    {
        var ora = CreateOracle();
        await using var oc = await ora.OpenAsync(default);
        await using var cmd = oc.CreateCommand();
        cmd.CommandText = "UPDATE HR.EMPLOYEES_SRC SET SALARY = :s WHERE EMPLOYEE_ID = :i";
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("s", newSalary));
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("i", id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertOracleSrcAsync(int id, string fn, string ln, int dept, decimal sal)
    {
        var ora = CreateOracle();
        await using var oc = await ora.OpenAsync(default);
        await using var cmd = oc.CreateCommand();
        cmd.CommandText = "INSERT INTO HR.EMPLOYEES_SRC (EMPLOYEE_ID, FIRST_NAME, LAST_NAME, DEPARTMENT_ID, SALARY, HIRE_DATE) VALUES (:i, :f, :l, :d, :s, SYSDATE)";
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("i", id));
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("f", fn));
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("l", ln));
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("d", dept));
        cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("s", sal));
        await cmd.ExecuteNonQueryAsync();
    }
}
