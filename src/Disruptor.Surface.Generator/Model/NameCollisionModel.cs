namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Which generated-name space two model types collided in. Every kind is fatal: the
/// colliding name is baked into DDL, <c>AddSource</c> hint names, or emitted member
/// names, so duplicates either merge two CLR types onto one physical table / edge or
/// crash the generator with a duplicate-hint <c>ArgumentException</c> (CS8785).
/// </summary>
public enum NameCollisionKind
{
    /// <summary>CG042 — two <c>[Table]</c> classes map to the same physical SurrealDB table name.</summary>
    TableName,

    /// <summary>CG043 — two forward relation kinds map to the same edge table name.</summary>
    EdgeName,

    /// <summary>CG044 — two <c>[AggregateRoot]</c> tables share a simple name (loader classes and <c>Load{Name}Async</c> members key on it).</summary>
    AggregateRootName,
}

/// <summary>
/// One pre-emit uniqueness violation, computed by
/// <see cref="Pipeline.RelationLinker"/> and rendered as CG042/CG043/CG044 by
/// <c>ModelGenerator.Emit</c>. <paramref name="ParticipantFullNames"/> is sorted
/// (ordinal) so equality is stable and "first participant wins" is deterministic for
/// the emitters that skip the colliding duplicates.
/// </summary>
public sealed record NameCollisionModel(
    NameCollisionKind Kind,
    string CollidingName,
    EquatableArray<string> ParticipantFullNames);

/// <summary>
/// A <c>[Table]</c> or <c>[CompositionRoot]</c> declared nested inside another type
/// (CG045). The emitters produce namespace-scoped partials only, so nested declarations
/// are pulled out of the model by the linker and reported instead of emitting an orphan
/// top-level partial (CS9248/CS9249 storm).
/// </summary>
/// <param name="Location">Identifier location of the nested declaration, captured at
/// extraction. Issue models represent an already-failing build, so the
/// position-sensitive <see cref="LocationInfo"/> cannot regress incremental caching.</param>
public sealed record NestedTypeIssueModel(
    string FullName,
    string AttributeName,
    LocationInfo? Location);

/// <summary>
/// A model attribute (<c>[Table]</c> / <c>[CompositionRoot]</c> / an on-class relation
/// kind attribute) applied to a <c>record</c> declaration (CG048). The FAWMN predicates
/// admit record declarations only so the transform can flag them; the linker pulls the
/// flagged models out of the graph and this issue explains why nothing was emitted —
/// previously the predicate silently skipped records (clean compile, zero output).
/// </summary>
/// <param name="Location">Identifier location of the record declaration, captured at
/// extraction (already-failing build — safe to embed).</param>
public sealed record RecordTypeIssueModel(
    string FullName,
    string AttributeName,
    LocationInfo? Location);

/// <summary>
/// A <c>[Table]</c> declared with type parameters (CG049). Generic tables are rejected
/// fail-closed: the physical table name derives from the simple class name and ignores
/// type arguments (two closed constructions would silently share one table), the
/// <c>{Name}Id</c> struct is non-generic, and the query/hydration root accessors cannot
/// name an open generic type. Previously the half-supported shape emitted a
/// <c>partial class Foo&lt;T&gt;</c> whose <c>MetadataName</c>-keyed FullName
/// (<c>Ns.Foo`1</c>) never matched any use-site lookup, so aggregate membership, CG014
/// cycle detection, and CG017/CG021 silently skipped it while the generated roots
/// referenced the open generic (CS0305).
/// </summary>
/// <param name="FullName">The table's full name (metadata form, e.g. <c>Ns.Foo`1</c>).</param>
/// <param name="SimpleName">The table's simple name (for the <c>{Name}Id</c> mention in the message).</param>
/// <param name="TypeParameters">Comma-joined type parameter names, e.g. <c>T, TKey</c>.</param>
/// <param name="Location">Identifier location of the generic declaration, captured at
/// extraction (already-failing build — safe to embed).</param>
public sealed record GenericTableIssueModel(
    string FullName,
    string SimpleName,
    string TypeParameters,
    LocationInfo? Location);
