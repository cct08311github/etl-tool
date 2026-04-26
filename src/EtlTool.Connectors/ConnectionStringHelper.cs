using EtlTool.Core.Models;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace EtlTool.Connectors;

public sealed class OracleParts
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1521;
    public string ServiceName { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SqlServerParts
{
    public string Server { get; set; } = "";
    public int Port { get; set; } = 1433;
    public string Database { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    public bool Encrypt { get; set; } = true;
}

/// <summary>
/// 用各 provider 的官方 ConnectionStringBuilder 在友善欄位 ↔ 連線字串之間轉換。
/// 解析失敗時回 null（讓 UI 切回手動模式）。
/// </summary>
public static class ConnectionStringHelper
{
    public static OracleParts DefaultOracle() => new()
    {
        Host = "localhost", Port = 1521, ServiceName = "XEPDB1",
    };

    public static SqlServerParts DefaultSqlServer() => new()
    {
        Server = "localhost", Port = 1433, UserId = "sa",
    };

    public static string Build(DbProviderType provider, OracleParts? oracle, SqlServerParts? sqlServer)
        => provider switch
        {
            DbProviderType.Oracle when oracle is not null => BuildOracle(oracle),
            DbProviderType.SqlServer when sqlServer is not null => BuildSqlServer(sqlServer),
            _ => "",
        };

    public static string BuildOracle(OracleParts p)
    {
        var b = new OracleConnectionStringBuilder
        {
            DataSource = $"{p.Host}:{p.Port}/{p.ServiceName}",
            UserID = p.UserId,
            Password = p.Password,
        };
        return b.ConnectionString;
    }

    public static string BuildSqlServer(SqlServerParts p)
    {
        var server = p.Port == 1433 ? p.Server : $"{p.Server},{p.Port}";
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = p.Database,
            UserID = p.UserId,
            Password = p.Password,
            TrustServerCertificate = p.TrustServerCertificate,
            Encrypt = p.Encrypt,
        };
        return b.ConnectionString;
    }

    public static OracleParts? TryParseOracle(string connectionString)
    {
        try
        {
            var b = new OracleConnectionStringBuilder(connectionString);
            var (host, port, service) = ParseOracleDataSource(b.DataSource ?? "");
            return new OracleParts
            {
                Host = host, Port = port, ServiceName = service,
                UserId = b.UserID ?? "", Password = b.Password ?? "",
            };
        }
        catch { return null; }
    }

    public static SqlServerParts? TryParseSqlServer(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            var (server, port) = ParseSqlServerDataSource(b.DataSource ?? "");
            return new SqlServerParts
            {
                Server = server, Port = port,
                Database = b.InitialCatalog ?? "",
                UserId = b.UserID ?? "", Password = b.Password ?? "",
                TrustServerCertificate = b.TrustServerCertificate,
                Encrypt = b.Encrypt != SqlConnectionEncryptOption.Optional,
            };
        }
        catch { return null; }
    }

    private static (string host, int port, string service) ParseOracleDataSource(string ds)
    {
        if (string.IsNullOrEmpty(ds)) return ("localhost", 1521, "XEPDB1");
        // 形式 1: host:port/service
        var slashIdx = ds.IndexOf('/');
        if (slashIdx > 0)
        {
            var hostPort = ds[..slashIdx];
            var service = ds[(slashIdx + 1)..];
            var colonIdx = hostPort.IndexOf(':');
            if (colonIdx > 0 && int.TryParse(hostPort[(colonIdx + 1)..], out var p))
                return (hostPort[..colonIdx], p, service);
            return (hostPort, 1521, service);
        }
        return (ds, 1521, "XEPDB1");
    }

    private static (string server, int port) ParseSqlServerDataSource(string ds)
    {
        if (string.IsNullOrEmpty(ds)) return ("localhost", 1433);
        var commaIdx = ds.IndexOf(',');
        if (commaIdx > 0 && int.TryParse(ds[(commaIdx + 1)..], out var p))
            return (ds[..commaIdx], p);
        return (ds, 1433);
    }
}
