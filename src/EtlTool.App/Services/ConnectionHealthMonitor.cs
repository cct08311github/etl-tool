using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// 對所有 ConnectionDefinition 定期 ping，把結果寫回 LastCheckedAt / LastCheckOk / LastCheckError。
/// 狀態翻轉時（OK → Fail 或 Fail → OK）發 audit event。
///
/// 設計：
///   - 每 N 分鐘跑一次（預設 5，可由 ConnectionHealth:IntervalMinutes 設定）
///   - 用獨立 DI scope，避免污染主流程
///   - Test 連線有 timeout（預設 10 秒），避免單一失效連線拖垮整批
///   - 啟動延遲 60 秒（讓 startup migration / Quartz init 完成）
/// </summary>
public sealed class ConnectionHealthMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ConnectionHealthMonitor> _log;

    public ConnectionHealthMonitor(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<ConnectionHealthMonitor> log)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _log = log;
    }

    private TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(1, _config.GetValue<int?>("ConnectionHealth:IntervalMinutes") ?? 5));

    private TimeSpan PingTimeout =>
        TimeSpan.FromSeconds(Math.Max(1, _config.GetValue<int?>("ConnectionHealth:TimeoutSeconds") ?? 10));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ConnectionHealthMonitor cycle failed; will retry next interval.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectorFactory>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        var connections = await db.Connections.ToListAsync(ct);

        foreach (var conn in connections)
        {
            if (ct.IsCancellationRequested) break;

            var prevOk = conn.LastCheckOk;
            (bool ok, string? error) result;

            try
            {
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(PingTimeout);
                var connector = factory.Create(conn);
                var pinged = await connector.TestConnectionAsync(pingCts.Token);
                result = pinged ? (true, null) : (false, "TestConnection 回傳 false");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // host shutting down
            }
            catch (OperationCanceledException)
            {
                result = (false, $"連線逾時 (>{PingTimeout.TotalSeconds:0}s)");
            }
            catch (Exception ex)
            {
                result = (false, ex.GetType().Name + ": " + ex.Message);
            }

            conn.LastCheckedAt = DateTime.UtcNow;
            conn.LastCheckOk = result.ok;
            conn.LastCheckError = result.ok ? null : Truncate(result.error, 1000);

            // 狀態翻轉時發 audit
            if (prevOk != result.ok)
            {
                if (result.ok)
                {
                    await audit.LogAsync(
                        AuditCategory.Connection, AuditAction.TestConnection,
                        $"連線「{conn.Name}」恢復正常",
                        targetType: nameof(ConnectionDefinition), targetId: conn.Id, targetName: conn.Name,
                        severity: AuditSeverity.Info, ct: ct);
                }
                else
                {
                    await audit.LogAsync(
                        AuditCategory.Connection, AuditAction.TestConnection,
                        $"連線「{conn.Name}」健康檢查失敗：{result.error}",
                        targetType: nameof(ConnectionDefinition), targetId: conn.Id, targetName: conn.Name,
                        severity: AuditSeverity.Warning,
                        detailsJson: System.Text.Json.JsonSerializer.Serialize(new { error = result.error }),
                        ct: ct);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
