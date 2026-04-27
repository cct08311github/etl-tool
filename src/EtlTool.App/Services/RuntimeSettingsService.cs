using EtlTool.Data.Repositories;

namespace EtlTool.App.Services;

/// <summary>
/// 對外提供「目前生效的設定值」— 讀取順序：
///   1. RuntimeSettings 表（admin 在 /system 頁改的）
///   2. IConfiguration（appsettings.json / 環境變數 / 部署時注入）
///   3. 程式碼預設值
///
/// Singleton + in-memory cache（TTL 30 秒）— 避免每次 webhook / rate-limit 檢查都打 DB。
/// 寫入時呼叫 <see cref="Invalidate"/> 強制下次重讀。
///
/// 為什麼要 cache？
///   - HttpFailureNotifier 每次 ETL 完成都要讀 webhook URL；rate limiter 每筆 API
///     request 都要讀 limit；不 cache 等於每動作多一次 DB query
///   - 30 秒是 banking 可接受的「設定生效延遲」（admin 改完到實際生效）
/// </summary>
public sealed class RuntimeSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = new();
    private DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public RuntimeSettingsService(IServiceScopeFactory scopeFactory, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _config = config;
    }

    /// <summary>
    /// 拿 string 設定值。DB 優先；DB 無此 key → fallback 到 appsettings；
    /// 都沒有 → 回 null。
    /// </summary>
    public string? GetString(string key)
    {
        var dict = LoadCacheIfStale();
        if (dict.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
        return _config[key];
    }

    public int GetInt(string key, int fallback)
    {
        var raw = GetString(key);
        return int.TryParse(raw, out var n) ? n : fallback;
    }

    public bool GetBool(string key, bool fallback)
    {
        var raw = GetString(key);
        if (string.IsNullOrEmpty(raw)) return fallback;
        return bool.TryParse(raw, out var b) ? b : fallback;
    }

    /// <summary>把 cache 清掉，下次 Get* 會重讀 DB。寫入端應在 commit 後呼叫。</summary>
    public void Invalidate()
    {
        lock (_lock) { _cachedAt = DateTime.MinValue; }
    }

    private Dictionary<string, string> LoadCacheIfStale()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheTtl) return _cache;
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<RuntimeSettingsRepository>();
            // 同步阻塞拉一次 — 30 秒一次，可接受
            _cache = repo.GetAllAsync(default).GetAwaiter().GetResult();
            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
    }
}
