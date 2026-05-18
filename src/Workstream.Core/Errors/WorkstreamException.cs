using System;

namespace Workstream.Core.Errors;

/// <summary>
/// Thrown when an operation cannot proceed. Carries a <see cref="WorkstreamError"/> so the
/// MCP tool boundary can serialize it directly into <c>{ ok: false, error: {...} }</c>.
/// </summary>
public sealed class WorkstreamException : Exception
{
    public WorkstreamError Error { get; }

    public WorkstreamException(WorkstreamError error)
        : base(error.Message)
    {
        Error = error;
    }
}
