namespace Disruptor.Surface.Runtime;

/// <summary>
/// Which operation closed a <see cref="SurrealSession"/>. Sessions are one-shot: the
/// first close wins and every subsequent public call throws
/// <see cref="InvalidOperationException"/> carrying <see cref="SessionCloseReason.Summary"/>.
/// </summary>
public enum SessionCloseKind
{
    /// <summary>The user called <see cref="SurrealSession.Abandon"/>.</summary>
    Abandoned,

    /// <summary><see cref="SurrealSession.SaveAsync"/> failed mid-dispatch.</summary>
    SaveFailed,

    /// <summary><see cref="SurrealSession.DeleteAsync"/> failed mid-dispatch (wire or planning error other than a Reject blocker).</summary>
    DeleteFailed,

    /// <summary>
    /// <see cref="SurrealSession.DeleteAsync"/>'s pre-flight cascade resolve found
    /// <see cref="ReferenceDeleteBehavior.Reject"/> blockers and threw
    /// <see cref="CascadeRejectException"/> before any wire dispatch.
    /// </summary>
    RejectedDelete,

    /// <summary><see cref="SurrealSession.UnrelateAsync{TKind}"/> failed mid-dispatch.</summary>
    UnrelateFailed,

    /// <summary><see cref="SurrealSession.FetchAsync{T}(Query.SurfaceQuery{T}, Disruptor.Surreal.SurrealClient, CancellationToken)"/> failed mid-dispatch or mid-hydration.</summary>
    FetchFailed,

    /// <summary>One of the async variant / edge-traversal query methods failed mid-dispatch or mid-hydration.</summary>
    QueryFailed,
}

/// <summary>
/// Why a <see cref="SurrealSession"/> is closed: the closing operation
/// (<see cref="Kind"/>), the entity/edge id it was operating on where one was available
/// (<see cref="EntityId"/>), and the original exception (<see cref="Cause"/> —
/// preserved as-is; the failing call itself threw it unwrapped). Stamped exactly once,
/// at the first site that closes the session; later close attempts (including
/// <see cref="SurrealSession.Abandon"/> on an already-closed session) don't overwrite it.
/// </summary>
public sealed record SessionCloseReason(
    SessionCloseKind Kind,
    RecordId? EntityId = null,
    Exception? Cause = null)
{
    /// <summary>
    /// One-line human-readable description, embedded in the closed-session
    /// <see cref="InvalidOperationException"/> message — e.g.
    /// <c>"SaveAsync of designs:x failed: SurrealRpcException: …"</c>.
    /// </summary>
    public string Summary
    {
        get
        {
            var of = EntityId is { } id ? $" of {id}" : "";
            var operation = Kind switch
            {
                SessionCloseKind.Abandoned => "Abandon was called",
                SessionCloseKind.SaveFailed => $"SaveAsync{of} failed",
                SessionCloseKind.DeleteFailed => $"DeleteAsync{of} failed",
                SessionCloseKind.RejectedDelete => $"DeleteAsync{of} was rejected by the pre-flight cascade resolve",
                SessionCloseKind.UnrelateFailed => $"UnrelateAsync{of} failed",
                SessionCloseKind.FetchFailed => $"FetchAsync{of} failed",
                SessionCloseKind.QueryFailed => $"an async edge query{of} failed",
                _ => $"an operation{of} failed",
            };
            return Cause is null
                ? operation
                : $"{operation}: {Cause.GetType().Name}: {Cause.Message}";
        }
    }
}
