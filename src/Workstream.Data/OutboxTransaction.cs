using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Workstream.Data;

/// <summary>
/// The transaction of a claim-gated state change, made ambient while the caller's
/// <c>inTransaction</c> callback runs. Outbox writes issued from the callback join it, so the
/// state change and its outbox rows commit or roll back together: a stale claim queues
/// nothing, and a crash can't drop the notification of a committed change.
/// </summary>
public static class OutboxTransaction
{
    private static readonly AsyncLocal<Scope?> _current = new();

    internal sealed record Scope(NpgsqlConnection Connection, NpgsqlTransaction Transaction);

    internal static Scope? Current => _current.Value;

    internal static async Task RunAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Func<Task> body)
    {
        var previous = _current.Value;
        _current.Value = new Scope(conn, tx);
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            _current.Value = previous;
        }
    }
}
