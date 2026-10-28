namespace Workstream.Mcp.Notifications;

/// <summary>
/// The transaction of a claim-gated state change, handed to the caller's <c>inTransaction</c>
/// callback. Outbox rows enqueued with it commit or roll back with that change, so a stale
/// claim queues nothing and a crash can't drop the notification of a committed change.
/// The scope closes when the callback returns; using it afterwards throws.
/// </summary>
public interface IOutboxScope
{
}
