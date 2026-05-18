using System.Diagnostics.Metrics;

namespace Workstream.Telemetry;

/// <summary>
/// Strongly-typed registrations for every metric named in §11.2. One <see cref="Meter"/>
/// per the conventional .NET pattern; all instruments are created once and reused.
/// </summary>
public sealed class WorkstreamMetrics
{
    public const string MeterName = "workstream";

    public Meter Meter { get; } = new(MeterName, "0.1.0");

    public Histogram<double> ToolDurationSeconds        { get; }
    public Counter<long>     ClaimContentionNoWork      { get; }
    public Counter<long>     ClaimContentionTotal       { get; }
    public Counter<long>     ClaimTtlExpirations        { get; }
    public ObservableGauge<long>   BoardSyncQueueDepth        { get; }
    public ObservableGauge<double> BoardSyncOldestPendingSec  { get; }
    public ObservableGauge<long>   SlackNotifyQueueDepth      { get; }
    public ObservableGauge<long>   DbPoolInUse                { get; }
    public ObservableGauge<long>   DbPoolWaiting              { get; }
    public ObservableGauge<double> DashboardCacheHitRatio     { get; }
    public Counter<long>     StateTransitionsTotal      { get; }
    public Counter<long>     TokenResolutionFailures    { get; }

    private long _boardSyncQueue;
    private double _boardOldestPendingSec;
    private long _slackQueue;
    private long _dbPoolInUse;
    private long _dbPoolWaiting;
    private double _dashboardHitRatio;

    public WorkstreamMetrics()
    {
        ToolDurationSeconds = Meter.CreateHistogram<double>("workstream_mcp_tool_duration_seconds",
            unit: "s", description: "MCP tool call duration");
        ClaimContentionNoWork = Meter.CreateCounter<long>("workstream_claim_contention_no_work_total",
            description: "claim_next_task calls that returned no work");
        ClaimContentionTotal = Meter.CreateCounter<long>("workstream_claim_contention_total",
            description: "total claim_next_task calls");
        ClaimTtlExpirations = Meter.CreateCounter<long>("workstream_claim_ttl_expirations_total",
            description: "claims that expired before completion");
        StateTransitionsTotal = Meter.CreateCounter<long>("workstream_state_transitions_total",
            description: "state machine transitions accepted");
        TokenResolutionFailures = Meter.CreateCounter<long>("workstream_token_resolution_failures_total",
            description: "URL token resolution failures");

        BoardSyncQueueDepth = Meter.CreateObservableGauge("workstream_board_sync_queue_depth",
            () => System.Threading.Interlocked.Read(ref _boardSyncQueue));
        BoardSyncOldestPendingSec = Meter.CreateObservableGauge("workstream_board_sync_oldest_pending_seconds",
            () => _boardOldestPendingSec);
        SlackNotifyQueueDepth = Meter.CreateObservableGauge("workstream_slack_notify_queue_depth",
            () => System.Threading.Interlocked.Read(ref _slackQueue));
        DbPoolInUse = Meter.CreateObservableGauge("workstream_db_pool_in_use",
            () => System.Threading.Interlocked.Read(ref _dbPoolInUse));
        DbPoolWaiting = Meter.CreateObservableGauge("workstream_db_pool_waiting",
            () => System.Threading.Interlocked.Read(ref _dbPoolWaiting));
        DashboardCacheHitRatio = Meter.CreateObservableGauge("workstream_dashboard_cache_hit_ratio",
            () => _dashboardHitRatio);
    }

    public void SetBoardSyncQueue(long depth) => System.Threading.Interlocked.Exchange(ref _boardSyncQueue, depth);
    public void SetBoardSyncOldestPending(double seconds) => _boardOldestPendingSec = seconds;
    public void SetSlackQueue(long depth) => System.Threading.Interlocked.Exchange(ref _slackQueue, depth);
    public void SetDbPool(long inUse, long waiting)
    {
        System.Threading.Interlocked.Exchange(ref _dbPoolInUse, inUse);
        System.Threading.Interlocked.Exchange(ref _dbPoolWaiting, waiting);
    }
    public void SetDashboardHitRatio(double ratio) => _dashboardHitRatio = ratio;
}
