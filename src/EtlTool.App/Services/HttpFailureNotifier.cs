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

    public Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct)
        => NotifyRunOutcomeAsync(task, run, ct);

    public async Task NotifyRunOutcomeAsync(EtlTask task, RunHistory run, CancellationToken ct)
    {
        var url = _config["Webhooks:OnFailure"];
        if (string.IsNullOrWhiteSpace(url)) return;

        var format = WebhookPayloadBuilder.ParseFormat(_config["Webhooks:Format"]);
        var payload = WebhookPayloadBuilder.BuildPayload(format, task, run, run.ErrorMessage);

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
            _log.LogError(ex, "Failure webhook POST threw (URL hidden for security)");
        }
    }
}
