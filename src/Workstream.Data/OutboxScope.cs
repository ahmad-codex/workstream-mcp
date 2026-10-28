using System;
using Npgsql;
using Workstream.Mcp.Notifications;

namespace Workstream.Data;

internal sealed class OutboxScope : IOutboxScope
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _closed;

    public OutboxScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public NpgsqlConnection Connection { get { EnsureOpen(); return _connection; } }

    public NpgsqlTransaction Transaction { get { EnsureOpen(); return _transaction; } }

    public void Close() => _closed = true;

    private void EnsureOpen()
    {
        if (_closed)
            throw new InvalidOperationException(
                "The outbox scope is closed: its transaction has already committed or rolled back. Enqueue outbox rows inside the inTransaction callback.");
    }
}
