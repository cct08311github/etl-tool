using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace EtlTool.App.Services;

/// <summary>
/// Prometheus metrics 註冊。所有 metrics 走全域 DefaultRegistry，
/// 由 prometheus-net 的 /metrics endpoint 自動 scrape。
///
/// Counters/Gauges：
///   etltool_runs_total{task_name, status}        — 累計執行次數（成功/失敗）
///   etltool_rows_read_total{task_name}           — 累計讀取列數
///   etltool_rows_written_total{task_name}        — 累計寫入列數
///   etltool_run_duration_seconds{task_name}      — Histogram (run wall-clock)
///   etltool_connection_health{connection_name}   — 1=ok, 0=fail, NaN=unchecked
///   etltool_scheduler_paused                     — 1=paused, 0=running
///   etltool_audit_events_total{category, severity}
/// </summary>
public sealed class EtlMetrics
{
    public static readonly Counter Runs = Metrics.CreateCounter(
        "etltool_runs_total",
        "Total ETL runs by task and status",
        new CounterConfiguration { LabelNames = new[] { "task_name", "status" } });

    public static readonly Counter RowsRead = Metrics.CreateCounter(
        "etltool_rows_read_total",
        "Total rows read by task",
        new CounterConfiguration { LabelNames = new[] { "task_name" } });

    public static readonly Counter RowsWritten = Metrics.CreateCounter(
        "etltool_rows_written_total",
        "Total rows written by task",
        new CounterConfiguration { LabelNames = new[] { "task_name" } });

    public static readonly Histogram RunDuration = Metrics.CreateHistogram(
        "etltool_run_duration_seconds",
        "ETL run wall-clock duration",
        new HistogramConfiguration
        {
            LabelNames = new[] { "task_name" },
            Buckets = new[] { 1.0, 5, 10, 30, 60, 120, 300, 600, 1800, 3600 },
        });

    public static readonly Gauge ConnectionHealth = Metrics.CreateGauge(
        "etltool_connection_health",
        "Connection health: 1=ok, 0=fail",
        new GaugeConfiguration { LabelNames = new[] { "connection_name" } });

    public static readonly Gauge SchedulerPaused = Metrics.CreateGauge(
        "etltool_scheduler_paused",
        "Scheduler global pause state: 1=paused, 0=running");

    public static readonly Counter AuditEvents = Metrics.CreateCounter(
        "etltool_audit_events_total",
        "Total audit events by category and severity",
        new CounterConfiguration { LabelNames = new[] { "category", "severity" } });
}
