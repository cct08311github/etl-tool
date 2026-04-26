using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// ETL 失敗時的通知通道。實作可以是 webhook、email、log-only。
/// 設計：fire-and-forget 風格，回傳前必須 catch 所有例外（不可影響呼叫端）。
/// </summary>
public interface IFailureNotifier
{
    Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct);
}

/// <summary>
/// 不做任何事的預設實作。Production 環境用 HTTP webhook 取代。
/// </summary>
public sealed class NoopFailureNotifier : IFailureNotifier
{
    public Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct) => Task.CompletedTask;
}
