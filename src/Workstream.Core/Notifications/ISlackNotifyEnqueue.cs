using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.StateMachine;

namespace Workstream.Mcp.Notifications;

/// <summary>
/// Enqueues a Slack notification for the outbox worker (§8.3). The implementation looks
/// up the project's Slack workspace and channel, applies the plan-type's slack_templates
/// to format the body, and writes one row to <c>slack_notify_log</c>.
/// </summary>
public interface ISlackNotifyEnqueue
{
    Task EnqueueForTaskAsync(
        Plan plan,
        (PlanType Row, StateGraph Graph) planType,
        WorkTask task,
        string notificationType,
        RequestContext ctx,
        IReadOnlyDictionary<string, string>? extraTokens = null,
        CancellationToken ct = default);

    Task EnqueueForFindingAsync(
        Plan plan,
        (PlanType Row, StateGraph Graph) planType,
        Finding finding,
        Guid taskId,
        string notificationType,
        RequestContext ctx,
        IReadOnlyDictionary<string, string>? extraTokens = null,
        CancellationToken ct = default);

    Task EnqueueForPlanAsync(
        Plan plan,
        (PlanType Row, StateGraph Graph) planType,
        string notificationType,
        RequestContext ctx,
        IReadOnlyDictionary<string, string>? extraTokens = null,
        CancellationToken ct = default);
}
