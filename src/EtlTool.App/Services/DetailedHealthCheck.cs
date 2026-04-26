using System.Text.Json;
using EtlTool.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;

namespace EtlTool.App.Services;

/// <summary>
/// 詳細的 /healthz/detail JSON：DB ping + Quartz 運行狀態 + 各 hosted service 上次心跳時間。
/// 給銀行 ops 監控系統用 — 簡單的 200/503 不夠，需要精細的 component 狀態。
/// </summary>
public static class DetailedHealthCheck
{
    public sealed record HealthDetail(
        string Status,
        DateTime CheckedAt,
        Dictionary<string, ComponentStatus> Components);

    public sealed record ComponentStatus(
        string Status,        // ok / warn / fail
        long? LatencyMs,
        string? Detail);

    public static async Task<HealthDetail> CollectAsync(IServiceProvider services, CancellationToken ct)
    {
        var components = new Dictionary<string, ComponentStatus>();

        // 1) DB 連線 + ping
        await CheckDbAsync(services, components, ct);

        // 2) Quartz scheduler 運作狀態
        await CheckQuartzAsync(services, components, ct);

        // 3) ConnectionHealthMonitor 上次跑成功時間（透過讀任一 connection 的 LastCheckedAt）
        await CheckConnectionMonitorAsync(services, components, ct);

        // 4) Audit chain integrity 最近結果（如果有 cache）
        // — 這個資訊在 Home 頁面會即時跑，這裡只看 audit table 是否寫得進去
        await CheckAuditWriteAsync(services, components, ct);

        var overall = components.Values.Any(c => c.Status == "fail") ? "fail"
            : components.Values.Any(c => c.Status == "warn") ? "warn"
            : "ok";

        return new HealthDetail(overall, DateTime.UtcNow, components);
    }

    private static async Task CheckDbAsync(IServiceProvider sp, Dictionary<string, ComponentStatus> result, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var canConnect = await db.Database.CanConnectAsync(ct);
            sw.Stop();
            result["database"] = canConnect
                ? new ComponentStatus("ok", sw.ElapsedMilliseconds, "SQLite reachable")
                : new ComponentStatus("fail", sw.ElapsedMilliseconds, "DB not reachable");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result["database"] = new ComponentStatus("fail", sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task CheckQuartzAsync(IServiceProvider sp, Dictionary<string, ComponentStatus> result, CancellationToken ct)
    {
        try
        {
            var factory = sp.GetService<ISchedulerFactory>();
            if (factory is null)
            {
                result["scheduler"] = new ComponentStatus("warn", null, "ISchedulerFactory not registered");
                return;
            }
            var scheduler = await factory.GetScheduler(ct);
            var standby = scheduler.InStandbyMode;
            var shutdown = scheduler.IsShutdown;
            var jobs = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup(), ct);

            if (shutdown)
                result["scheduler"] = new ComponentStatus("fail", null, "scheduler shut down");
            else if (standby)
                result["scheduler"] = new ComponentStatus("warn", null, $"in standby (jobs={jobs.Count})");
            else
                result["scheduler"] = new ComponentStatus("ok", null, $"running, jobs={jobs.Count}");
        }
        catch (Exception ex)
        {
            result["scheduler"] = new ComponentStatus("fail", null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task CheckConnectionMonitorAsync(IServiceProvider sp, Dictionary<string, ComponentStatus> result, CancellationToken ct)
    {
        try
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 找最近一次 LastCheckedAt — 若存在連線但都沒檢查 → warn；若有檢查 但 > 15 分鐘前 → warn；否則 ok。
            var totalConns = await db.Connections.CountAsync(ct);
            if (totalConns == 0)
            {
                result["connection_monitor"] = new ComponentStatus("ok", null, "no connections to check");
                return;
            }
            var latest = await db.Connections
                .Where(c => c.LastCheckedAt != null)
                .Select(c => c.LastCheckedAt!.Value)
                .OrderByDescending(t => t)
                .FirstOrDefaultAsync(ct);
            if (latest == default)
            {
                result["connection_monitor"] = new ComponentStatus("warn", null, $"no connection has been checked yet (count={totalConns})");
                return;
            }
            var ageMin = (int)(DateTime.UtcNow - latest).TotalMinutes;
            if (ageMin > 15)
                result["connection_monitor"] = new ComponentStatus("warn", null, $"latest check {ageMin} minutes ago (expected ~5 min)");
            else
                result["connection_monitor"] = new ComponentStatus("ok", null, $"latest check {ageMin} minutes ago");
        }
        catch (Exception ex)
        {
            result["connection_monitor"] = new ComponentStatus("fail", null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task CheckAuditWriteAsync(IServiceProvider sp, Dictionary<string, ComponentStatus> result, CancellationToken ct)
    {
        try
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var latest = await db.AuditEvents
                .OrderByDescending(e => e.At)
                .Select(e => e.At)
                .FirstOrDefaultAsync(ct);
            if (latest == default)
            {
                result["audit"] = new ComponentStatus("warn", null, "no audit events yet (fresh install?)");
                return;
            }
            var ageHours = (int)(DateTime.UtcNow - latest).TotalHours;
            result["audit"] = new ComponentStatus("ok", null, $"latest event {(ageHours < 1 ? "<1" : ageHours.ToString())} hour(s) ago");
        }
        catch (Exception ex)
        {
            result["audit"] = new ComponentStatus("fail", null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>把 HealthDetail 寫進 HttpResponse 為 JSON，並回傳對應 HTTP status code。</summary>
    public static async Task WriteJsonAsync(HttpContext ctx, HealthDetail detail)
    {
        ctx.Response.StatusCode = detail.Status switch
        {
            "fail" => 503,
            "warn" => 200,   // warn 仍回 200，但 status 內容反映；ops 系統可解析 JSON 細節
            _ => 200,
        };
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true });
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }
}
