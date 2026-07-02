using Disruptor.Surreal;
using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Per-entity Save context. Generator-emitted <see cref="IEntity.SaveAsync"/> bodies use
/// this to recurse into dependencies, check whether an entity is already known, dispatch
/// SurrealQL into the active transaction, and mark themselves as saved.
/// </summary>
/// <remarks>
/// The session is the orchestration boundary; entities just describe their own structure.
/// <see cref="SurrealSession.SaveAsync(IEntity, SurrealTransaction, System.Threading.CancellationToken)"/>
/// constructs an <see cref="ISaveContext"/>, calls the root entity's
/// <see cref="IEntity.SaveAsync"/>, and lets recursion drive itself through the
/// <see cref="SaveAsync"/> callback back into the session.
/// </remarks>
public interface ISaveContext
{
    /// <summary>The transaction to dispatch into. Entities use this for their own RPC sends.</summary>
    SurrealTransaction Transaction { get; }

    /// <summary>
    /// The instant source for <c>[CreatedAt]</c> / <c>[UpdatedAt]</c> audit stamping.
    /// Generator-emitted <see cref="IEntity.SaveAsync"/> bodies read this once per
    /// dispatch, so CREATE stamps created + updated with the same instant. Default
    /// interface member returns <see cref="DateTimeOffset.UtcNow"/> — the session's
    /// private <see cref="ISaveContext"/> implementation picks it up unmodified; test
    /// fakes override it for deterministic clocks.
    /// </summary>
    DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>
    /// True iff <paramref name="id"/> is in the session's identity map — either because it
    /// was loaded from the DB, or because Save has already dispatched a CREATE for it in
    /// this pass. Forward-reference walks check this to decide whether to recurse.
    /// </summary>
    bool IsTracked(IRecordId id);

    /// <summary>
    /// Recursive Save callback. Per-entity SaveAsync invokes this for forward dependencies
    /// and new children. The session orchestrates the recursion (cycle detection, ordering)
    /// so each entity stays a structural describer.
    /// </summary>
    Task SaveAsync(IEntity entity, CancellationToken ct);

    /// <summary>
    /// Mark <paramref name="entity"/> as saved in this Save pass. Adds the entity to the
    /// identity map (if not already present) so subsequent <see cref="IsTracked"/> checks
    /// see it. Generator-emitted SaveAsync calls this after dispatching its own CREATE/UPDATE.
    /// </summary>
    void MarkSaved(IEntity entity);

    // ─────────────────────────── dispatch seam ───────────────────────────
    // Every wire send an emitted IEntity.SaveAsync body performs goes through one of
    // the three members below rather than calling ctx.Transaction directly. The
    // defaults ARE the single-save behavior (immediate dispatch through Transaction);
    // SurrealSession's batch SaveAsync overload substitutes a buffering context that
    // coalesces contiguous same-table plain creates into one INSERT statement and
    // flushes the buffer before any other dispatch, so wire-statement order (and
    // therefore in-transaction visibility) is preserved.

    /// <summary>
    /// Dispatch a plain CREATE of a new record — the only dispatch kind that is safe
    /// to batch (it has no per-dispatch result the emitted body inspects, and new
    /// records — including <c>[Version]</c>-seeded ones, which CREATE with version 1
    /// unguarded — carry no conflict guard). Default: immediate
    /// <see cref="SurrealTransaction.CreateAsync"/> (<c>CREATE $_record_id CONTENT $_content</c>).
    /// </summary>
    ValueTask DispatchCreateAsync(RecordId id, SurrealObject content, CancellationToken ct)
        => new(Transaction.CreateAsync(id.ToSdk(), content, ct));

    /// <summary>
    /// Dispatch an UPSERT of an already-tracked record. Never batched — updates must
    /// land in their original order relative to the creates they may depend on.
    /// Default: immediate <see cref="SurrealTransaction.UpsertAsync"/>
    /// (<c>UPSERT $_record_id CONTENT $_content</c>).
    /// </summary>
    ValueTask DispatchUpsertAsync(RecordId id, SurrealObject content, CancellationToken ct)
        => new(Transaction.UpsertAsync(id.ToSdk(), content, ct));

    /// <summary>
    /// Dispatch a raw SurrealQL statement whose response the emitted body inspects —
    /// the <c>[Version]</c>-guarded <c>UPDATE … WHERE version = $_expected_version</c>
    /// (each needs its own empty-result conflict check) and the relation-variant
    /// <c>INSERT RELATION INTO …</c>. Never batched. Default: immediate
    /// <see cref="SurrealTransaction.QueryAsync"/>.
    /// </summary>
    ValueTask<SurrealQueryResponse> DispatchQueryAsync(string sql, SurrealObject bindings, CancellationToken ct)
        => new(Transaction.QueryAsync(sql, bindings, ct));
}
