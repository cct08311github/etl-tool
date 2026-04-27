using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// 每日執行 SQLite VACUUM INTO 寫入備份檔，並依 retention 設定刪除超齡檔案。
///
/// 設定:
///   Backup:Enabled              — 預設 true
///   Backup:Directory            — 預設 &lt;DataDir&gt;/backups
///   Backup:HourLocal            — 預設 03（避免 03:00 audit / 03:30 run / 03:15 approval 的時段）
///   Backup:MinuteLocal          — 預設 45
///   Backup:RetainCount          — 保留最近 N 份（預設 14）；&lt;=0 視為不刪
///
/// 啟動延遲 120 秒避免 startup contention，跑一次 then daily.
/// </summary>
public sealed class NightlyBackupService : BackgroundService
{
    private const int DefaultHour = 3;
    private const int DefaultMinute = 45;
    private const int DefaultRetainCount = 14;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly ILogger<NightlyBackupService> _log;

    public NightlyBackupService(
        IServiceScopeFactory scopeFactory, IConfiguration config,
        IHostEnvironment env, ILogger<NightlyBackupService> log)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _env = env;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!(_config.GetValue<bool?>("Backup:Enabled") ?? true))
        {
            _log.LogInformation("NightlyBackupService disabled (Backup:Enabled = false).");
            return;
        }

        // 啟動延遲 120 秒
        try { await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // 啟動時跑一次 — 若 N 天沒備份就立刻補一份
        await TryBackupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var hour = _config.GetValue<int?>("Backup:HourLocal") ?? DefaultHour;
            var minute = _config.GetValue<int?>("Backup:MinuteLocal") ?? DefaultMinute;
            var nextRun = NextLocalRun(DateTime.Now, hour, minute);
            var wait = nextRun - DateTime.Now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.FromMinutes(1);

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await TryBackupAsync(stoppingToken);
        }
    }

    private async Task TryBackupAsync(CancellationToken ct)
    {
        try { await BackupOnceAsync(ct); }
        catch (Exception ex) { _log.LogError(ex, "Nightly backup failed; will retry next cycle."); }
    }

    private async Task BackupOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        var dataDir = _config["DataDirectory"]
                      ?? Environment.GetEnvironmentVariable("ETLTOOL_DATA_DIR")
                      ?? Path.Combine(_env.ContentRootPath, "data");
        var backupDir = _config["Backup:Directory"] ?? Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backupDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"etltool-{stamp}.db";
        var fullPath = Path.Combine(backupDir, fileName);

        // VACUUM INTO 是 SQLite 提供的 atomic snapshot — 不需停服務
        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO @t";
            var p = cmd.CreateParameter();
            p.ParameterName = "@t";
            p.Value = fullPath;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        long size = 0;
        try { size = new FileInfo(fullPath).Length; } catch { }

        await audit.LogAsync(AuditCategory.System, AuditAction.Update,
            $"📦 完成 nightly SQLite 備份：{fileName}（{size / 1024.0:F0} KB）",
            severity: AuditSeverity.Info, actor: "system", ct: ct);

        // Retention：保留最新 N 份，刪除其餘（依檔名字典序倒序≈時序倒序）
        var retain = _config.GetValue<int?>("Backup:RetainCount") ?? DefaultRetainCount;
        if (retain > 0) PruneOldBackups(backupDir, retain);

        _log.LogInformation("Nightly backup written: {Path} ({Size} bytes); retain {Retain}",
            fullPath, size, retain);
    }

    /// <summary>把 backupDir 下的 etltool-*.db 檔依字典序倒序，保留最新 N 份，其餘刪除。</summary>
    public static int PruneOldBackups(string backupDir, int retainCount)
    {
        if (retainCount <= 0) return 0;
        if (!Directory.Exists(backupDir)) return 0;

        var files = Directory.GetFiles(backupDir, "etltool-*.db")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

        var deleted = 0;
        foreach (var f in files.Skip(retainCount))
        {
            try { File.Delete(f); deleted++; } catch { /* 個別檔刪不掉就跳過 */ }
        }
        return deleted;
    }

    public static DateTime NextLocalRun(DateTime now, int hourLocal, int minuteLocal)
    {
        var todayRun = new DateTime(now.Year, now.Month, now.Day, hourLocal, minuteLocal, 0, DateTimeKind.Local);
        return now < todayRun ? todayRun : todayRun.AddDays(1);
    }
}
