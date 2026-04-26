using System.Text.Json;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Services;

/// <summary>
/// 把 ETL 失敗事件 POST 成 JSON 到設定的 webhook URL。
/// 適用：Slack incoming-webhook、Teams、PagerDuty Events API、自家 alert 接口。
///
/// 設計：
///   - 從 Webhooks:OnFailure 讀 URL；空字串 = 完全 no-op
///   - HTTP 5 秒 timeout，避免拖慢失敗處理
///   - 失敗只 log 不 throw — webhook 掛掉不能影響 ETL 失敗本身
///   - Slack 兼容格式：{"text": "..."} + 詳細欄位 in attachment
/// </summary>
public sealed class HttpFailureNotifier : IFailureNotifier
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<HttpFailureNotifier> _log;

    public HttpFailureNotifier(IHttpClientFactory httpFactory, IConfiguration config, ILogger<HttpFailureNotifier> log)
    {
        _httpFactory = httpFactory;
        _config = config;
        _log = log;
    }

    public async Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct)
    {
        var url = _config["Webhooks:OnFailure"];
        if (string.IsNullOrWhiteSpace(url)) return;

        var payload = new
        {
            text = $"⚠ ETL 任務失敗：「{task.Name}」",
            task_id = task.Id.ToString(),
            task_name = task.Name,
            run_id = run.Id.ToString(),
            status = run.Status.ToString(),
            trigger_type = run.TriggerType.ToString(),
            started_at = run.StartedAt.ToString("o"),
            finished_at = run.FinishedAt?.ToString("o"),
            rows_read = run.RowsRead,
            rows_written = run.RowsWritten,
            error = TruncateError(run.ErrorMessage, 1000),
        };

        try
        {
            using var http = _httpFactory.CreateClient("FailureWebhook");
            http.Timeout = TimeSpan.FromSeconds(5);
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");
            using var resp = await http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Failure webhook returned non-success: {Status} {Reason}",
                    (int)resp.StatusCode, resp.ReasonPhrase);
            }
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("Failure webhook timed out after 5s (url hidden).");
        }
        catch (Exception ex)
        {
            // 故意不重新拋；webhook 失敗不能拖累 ETL 失敗的處理
            _log.LogError(ex, "Failure webhook POST threw (URL hidden for security)");
        }
    }

    private static string? TruncateError(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max] + "… (truncated)";
    }
}
