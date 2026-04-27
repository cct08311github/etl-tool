using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Services;

/// <summary>
/// 把 ETL 執行結果 POST 成 JSON 到設定的 webhook URL。
/// 支援 Slack（attachments + color）/ Teams (MessageCard) / 純通用 JSON。
///
/// 設計：
///   - 從 Webhooks:OnFailure 讀 URL；空字串 = 完全 no-op
///   - 從 Webhooks:Format 讀 "slack" / "teams" / "generic"（預設 generic）
///   - HTTP 5 秒 timeout，避免拖慢失敗處理
///   - 失敗只 log 不 throw — webhook 掛掉不能影響 ETL 失敗本身
///   - 對 Recovery / 高 streak 有不同視覺 emphasis（透過 ErrorMessage 前綴判斷）
///
/// HMAC 簽章（Stripe-style，receiver 可驗證來源真的是 EtlTool 而非偽造）：
///   - 設了 Webhooks:SigningSecret → 每個 POST 都加：
///       X-Etl-Timestamp: &lt;unix_seconds&gt;
///       X-Etl-Signature: sha256=&lt;hex&gt; （HMAC-SHA256("&lt;ts&gt;.&lt;body&gt;", secret)）
///   - 沒設 → 不加 header（向後相容；Slack/Teams 不會檢查反正）
///   - 自己寫 receiver 時：先驗證 timestamp 與 now 差距 ≤ 5 分（防 replay），
///     再用同樣的 secret 算 HMAC 比對 hex（建議 timing-safe compare）。
/// </summary>
public sealed class HttpFailureNotifier : IFailureNotifier
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<HttpFailureNotifier> _log;
    private readonly RuntimeSettingsService? _settings;

    public HttpFailureNotifier(IHttpClientFactory httpFactory, IConfiguration config, ILogger<HttpFailureNotifier> log,
        RuntimeSettingsService? settings = null)
    {
        _httpFactory = httpFactory;
        _config = config;
        _log = log;
        _settings = settings;
    }

    /// <summary>讀設定：先 DB（執行期可調），後 appsettings（fallback）。</summary>
    private string? GetCfg(string key) => _settings is null ? _config[key] : _settings.GetString(key);

    /// <summary>
    /// 給 admin /system 頁的「Test webhook」按鈕用：
    /// 用一筆假的成功 run 打 webhook，回 (ok, statusCode, errorMessage)。
    /// 不寫 audit、不影響任何狀態 — 純配置驗證。
    /// </summary>
    public async Task<(bool Ok, int? StatusCode, string? Error)> TestAsync(CancellationToken ct)
    {
        var url = GetCfg("Webhooks:OnFailure");
        if (string.IsNullOrWhiteSpace(url))
            return (false, null, "Webhooks:OnFailure 未設定");

        var format = WebhookPayloadBuilder.ParseFormat(GetCfg("Webhooks:Format"));
        var fakeTask = new EtlTool.Core.Models.EtlTask
        {
            Id = Guid.NewGuid(),
            Name = "[TEST] Webhook configuration test",
        };
        var fakeRun = new EtlTool.Core.Models.RunHistory
        {
            Id = Guid.NewGuid(),
            EtlTaskId = fakeTask.Id,
            Status = EtlTool.Core.Models.RunStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            FinishedAt = DateTime.UtcNow,
            ErrorMessage = "[TEST] This is a webhook configuration test from /system page. No actual ETL ran.",
            TriggerType = EtlTool.Core.Models.TriggerType.Manual,
        };
        var payload = WebhookPayloadBuilder.BuildPayload(format, fakeTask, fakeRun, fakeRun.ErrorMessage);

        try
        {
            using var http = _httpFactory.CreateClient("FailureWebhook");
            http.Timeout = TimeSpan.FromSeconds(5);
            using var req = BuildSignedRequest(url, payload);
            using var resp = await http.SendAsync(req, ct);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, resp.IsSuccessStatusCode ? null : resp.ReasonPhrase);
        }
        catch (TaskCanceledException)
        {
            return (false, null, "Timeout (5s)");
        }
        catch (Exception ex)
        {
            return (false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct)
        => NotifyRunOutcomeAsync(task, run, ct);

    public async Task NotifyRunOutcomeAsync(EtlTask task, RunHistory run, CancellationToken ct)
    {
        var url = GetCfg("Webhooks:OnFailure");
        if (string.IsNullOrWhiteSpace(url)) return;

        var format = WebhookPayloadBuilder.ParseFormat(GetCfg("Webhooks:Format"));
        var payload = WebhookPayloadBuilder.BuildPayload(format, task, run, run.ErrorMessage);

        try
        {
            using var http = _httpFactory.CreateClient("FailureWebhook");
            http.Timeout = TimeSpan.FromSeconds(5);
            using var req = BuildSignedRequest(url, payload);
            using var resp = await http.SendAsync(req, ct);
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
            _log.LogError(ex, "Failure webhook POST threw (URL hidden for security)");
        }
    }

    /// <summary>
    /// 建構 POST request 並（若有設 secret）加上 HMAC-SHA256 簽章 header。
    /// 簽章涵蓋「&lt;timestamp&gt;.&lt;body&gt;」以防 replay attack（receiver 應同時驗 timestamp 在合理範圍）。
    /// </summary>
    internal HttpRequestMessage BuildSignedRequest(string url, object payload)
    {
        var bodyJson = JsonSerializer.Serialize(payload);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };

        var secret = GetCfg("Webhooks:SigningSecret");
        if (!string.IsNullOrEmpty(secret))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signedPayload = $"{ts}.{bodyJson}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
            var hex = Convert.ToHexString(sig).ToLowerInvariant();
            req.Headers.TryAddWithoutValidation("X-Etl-Timestamp", ts);
            req.Headers.TryAddWithoutValidation("X-Etl-Signature", $"sha256={hex}");
        }

        return req;
    }

    /// <summary>
    /// 給 unit test 用：暴露給定 secret + body 的 hex signature，
    /// 確認 receiver 實作能對得上。
    /// </summary>
    public static string ComputeSignatureForTesting(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(sig).ToLowerInvariant();
    }
}
