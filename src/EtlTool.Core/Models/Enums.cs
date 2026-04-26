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
