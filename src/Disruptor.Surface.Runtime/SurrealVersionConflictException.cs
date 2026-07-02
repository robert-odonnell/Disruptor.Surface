namespace Disruptor.Surface.Runtime;

/// <summary>
/// Thrown by a generator-emitted <see cref="IEntity.SaveAsync"/> body when a
/// <c>[Version]</c>-guarded UPDATE (<c>UPDATE record:id CONTENT { … } WHERE version =
/// $expected</c>) matches no row — a competing writer bumped the version between this
/// session's load and its save, so dispatching the content would silently overwrite the
/// other writer's changes (a lost update). The row is left untouched; the throw
/// propagates through <see cref="SurrealSession.SaveAsync(IEntity, Disruptor.Surreal.SurrealTransaction, System.Threading.CancellationToken)"/>'s
/// fail-closed catch, which closes the session. Reload a fresh session, reapply the
/// intent, and save again.
/// <para>
/// This complements the substrate's own <c>Disruptor.Surreal.SurrealConflictException</c>
/// (MVCC conflict between concurrent in-flight transactions at COMMIT): the version guard
/// covers the human-scale window — load, edit for minutes, save — where both writers'
/// transactions never overlap and MVCC sees no conflict.
/// </para>
/// </summary>
public sealed class SurrealVersionConflictException(RecordId entityId, long expectedVersion)
    : Exception($"Optimistic-concurrency conflict on {entityId}: the guarded UPDATE matched no row with version = {expectedVersion}. Another writer has modified the record since it was loaded; reload and retry.")
{
    /// <summary>The record whose guarded UPDATE found no matching row.</summary>
    public RecordId EntityId { get; } = entityId;

    /// <summary>The version this session loaded (and expected to still be current).</summary>
    public long ExpectedVersion { get; } = expectedVersion;
}
