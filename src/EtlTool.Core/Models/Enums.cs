namespace EtlTool.Core.Models;

public enum DbProviderType
{
    Oracle = 1,
    SqlServer = 2,
}

public enum WriteMode
{
    DeleteInsert = 1,
    Upsert = 2,
}

public enum FilterMode
{
    FormBuilder = 1,
    RawSql = 2,
}

public enum RunStatus
{
    Running = 1,
    Success = 2,
    Failed = 3,
}

public enum TriggerType
{
    Scheduled = 1,
    Manual = 2,
    Retry = 3,
}

public enum SchemaDriftPolicy
{
    /// <summary>不檢查 schema drift。</summary>
    Ignore = 0,
    /// <summary>檢查並 audit warning，但仍執行。</summary>
    Warn = 1,
    /// <summary>檢查到 mapping 受影響的 drift 就 fail-fast，不執行 ETL。</summary>
    Fail = 2,
}
