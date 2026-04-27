namespace EtlTool.Core.Models;

/// <summary>
/// 來源類型：DB connection 或檔案路徑。
/// 預設 Database — 既有 task 不需要 migration（新增欄位 default 0）。
/// </summary>
public enum SourceKind
{
    Database = 0,
    File = 1,
}

/// <summary>
/// 檔案格式。CSV / Excel(.xlsx) / 自訂分隔字元的 plain text。
/// </summary>
public enum FileSourceFormat
{
    Csv = 0,
    Excel = 1,
    DelimitedText = 2,
}

/// <summary>
/// 處理完成後的動作：
///   - None: 留在原地（下次掃描會再讀同一筆檔；建議搭配 glob pattern 與時間戳檔名避免重複）
///   - Archive: 移到 ArchiveDirectory，加 yyyyMMddHHmmss 後綴
///   - Delete: 刪除（銀行 ops 通常不愛這個 — 寧可 archive 留 audit）
/// </summary>
public enum FilePostAction
{
    None = 0,
    Archive = 1,
    Delete = 2,
}

/// <summary>
/// 檔案來源設定。序列化成 JSON 存在 EtlTask.FileSourceConfigJson，
/// 在 EtlEngine 執行前還原成這個物件。
///
/// 工作流：
///   1. 排程觸發 → FileSourceScanner 掃 <see cref="DirectoryPath"/> 下符合 <see cref="GlobPattern"/> 的檔
///   2. 排序選一個（最舊優先 = oldest first，符合 FIFO 保證；可改設定）
///   3. 用對應 reader 讀成 IDataReader → 沿用既有 BulkWriter / UpsertWriter 寫到 target
///   4. 處理完依 <see cref="PostAction"/> 動作：archive / delete / 不動
///   5. RunHistory 記下處理的檔名 + 大小 + 行數
///
/// 銀行 ops 強烈建議用 Archive 而非 Delete — 失敗時可以回放，audit 也有跡可循。
/// </summary>
public sealed class FileSourceConfig
{
    /// <summary>掃描根目錄（容器路徑或 UNC 都可）— 例：/data/inbox 或 \\fileserver\etl\inbox</summary>
    public string DirectoryPath { get; set; } = "";

    /// <summary>檔名 glob — 例：orders_*.csv / *.xlsx / *.txt（預設 *.csv）</summary>
    public string GlobPattern { get; set; } = "*.csv";

    public FileSourceFormat Format { get; set; } = FileSourceFormat.Csv;

    /// <summary>檔案編碼（CSV / DelimitedText 用；Excel 自帶編碼）— 預設 utf-8</summary>
    public string Encoding { get; set; } = "utf-8";

    /// <summary>第一行是否為欄位名（CSV / DelimitedText / Excel 都套用）</summary>
    public bool HasHeader { get; set; } = true;

    /// <summary>欄位分隔字元（CSV/DelimitedText 用）— 例：, 或 \t（用字面 \t 表示 tab）</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>Excel 用：sheet 名稱（空字串 = 第 1 個 sheet）</summary>
    public string ExcelSheetName { get; set; } = "";

    public FilePostAction PostAction { get; set; } = FilePostAction.Archive;

    /// <summary>Archive 用：歸檔目錄（空字串 = DirectoryPath/archive）</summary>
    public string ArchiveDirectory { get; set; } = "";

    /// <summary>每次排程觸發處理幾個檔（多檔模式）— 預設 1，避免一次跑爆。0 = 不限。</summary>
    public int MaxFilesPerRun { get; set; } = 1;
}
