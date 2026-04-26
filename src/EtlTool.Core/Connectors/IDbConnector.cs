using System.Data;
using System.Data.Common;
using EtlTool.Core.Models;

namespace EtlTool.Core.Connectors;

/// <summary>
/// 兩種 DB 共通的最小存取面：拓樸（schemas/tables/columns）+ 連線開啟 + 寫入策略。
/// 讀取走標準 DbCommand/DbDataReader，於 EtlEngine 內統一處理。
/// </summary>
public interface IDbConnector
{
    DbProviderType Provider { get; }

    Task<DbConnection> OpenAsync(CancellationToken ct);

    Task<bool> TestConnectionAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListTablesAsync(string schema, CancellationToken ct);
    Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string schema, string table, CancellationToken ct);

    /// <summary>傳回主鍵欄位名稱清單；若無 PK 則回空清單。</summary>
    Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(string schema, string table, CancellationToken ct);

    /// <summary>欄位/物件名稱引用（Oracle: "X"、SqlServer: [X]）</summary>
    string QuoteIdentifier(string name);

    /// <summary>schema.table 格式化（含引用）</summary>
    string QuoteQualified(string schema, string table);

    /// <summary>參數前置字元（Oracle ":"、SqlServer "@"）</summary>
    string ParameterPrefix { get; }

    /// <summary>建立此 provider 的命令參數（與 ADO.NET DbParameter 相容）</summary>
    DbParameter CreateParameter(string name, object? value);

    /// <summary>建立批量寫入器（DeleteInsert 走插入；Upsert 透過 staging + MERGE）</summary>
    IBulkWriter CreateBulkWriter(DbConnection connection, DbTransaction transaction);

    /// <summary>建立 MERGE/Upsert 寫入器</summary>
    IUpsertWriter CreateUpsertWriter(DbConnection connection, DbTransaction transaction);

    /// <summary>產生「最多 limit 筆」的 SELECT 語句（dry-run 預覽用）。實作各自處理 TOP / FETCH FIRST 差異。</summary>
    string LimitedSelect(string columnList, string fromQualified, string? whereClause, int limit);
}

public interface IBulkWriter : IAsyncDisposable
{
    /// <summary>把整批 staged 資料寫入目標表（一次 batch）。</summary>
    Task<int> WriteBatchAsync(
        string schema,
        string table,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<object?[]> rows,
        CancellationToken ct);
}

public interface IUpsertWriter : IAsyncDisposable
{
    /// <summary>依主鍵欄位 upsert 一個 batch（存在更新、不存在新增）。回傳影響筆數（受影響列）。</summary>
    Task<int> UpsertBatchAsync(
        string schema,
        string table,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<object?[]> rows,
        CancellationToken ct);
}
