namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Which annotation drove this property into the model. A single property can, in principle,
/// appear multiple times in the owning table — once per kind — though in practice only one
/// kind is meaningful. Kept as a flag enum so future combinations are cheap.
/// </summary>
[Flags]
public enum PropertyKind
{
    None       = 0,
    Id         = 1 << 0,
    Property   = 1 << 1,
    Parent     = 1 << 2,
    Children   = 1 << 3,
    Reference  = 1 << 4,
}

/// <summary>
/// Library-managed value overlays on a scalar <c>[Property]</c> field. Flags so the
/// validation pass can detect (and reject with CG055) a property carrying more than one
/// marker; a valid property carries at most one.
/// </summary>
[Flags]
public enum AutoValueKind
{
    None      = 0,
    /// <summary>[CreatedAt] — stamped with the save instant on CREATE dispatch only.</summary>
    CreatedAt = 1 << 0,
    /// <summary>[UpdatedAt] — stamped with the save instant on every dispatch.</summary>
    UpdatedAt = 1 << 1,
    /// <summary>[Version] — optimistic-concurrency counter: 1 on CREATE, n+1 via a WHERE-guarded UPDATE.</summary>
    Version   = 1 << 2,
}

/// <summary>
/// What happens to a referencing record when the referenced record is deleted. Mirrors
/// the runtime's <c>ReferenceDeleteBehavior</c> enum — names kept in lockstep so the
/// generator can emit references to the runtime type directly.
/// </summary>
public enum ReferenceDeletePolicy
{
    Reject,
    Unset,
    Cascade,
    Ignore,
}

/// <summary>
/// Which side of a relation kind a property participates on. Set for properties carrying
/// a <c>ForwardRelation</c>- or <c>InverseRelation&lt;T&gt;</c>-derived attribute; otherwise
/// <see cref="None"/>. Properties are the only carrier — relation attrs are
/// <c>AttributeTargets.Property</c> only.
/// </summary>
public enum RelationRole
{
    None,
    ForwardRelation,
    InverseRelation,
}

/// <summary>
/// Equatable snapshot of a property declared on a <c>[Table]</c> type that carries at least
/// one model annotation. Holds only strings/value-types so it survives incremental caching.
/// </summary>
/// <param name="RelationRole">
/// Non-<see cref="RelationRole.None"/> when the property also carries a forward/inverse
/// relation attribute — e.g. <c>[RestrictedBy] IReadOnlyCollection&lt;Constraint&gt; Restrictions { get; }</c>.
/// </param>
/// <param name="InlineMembers">
/// For inline-element collection <c>[Property]</c> shapes (<c>IReadOnlyList&lt;T&gt;</c> /
/// <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c> of records / POCOs), the persistable
/// members of <c>T</c>: public, instance, non-indexer, readable, schema-mappable, and
/// covered by <paramref name="InlineConstruction"/> (constructor-positional or settable).
/// Drives <c>scenarios.*.kind</c>-style sub-field DDL in the schema emitter and the typed
/// per-element Hydrate / Save in the partial emitter. Empty for any other kind of
/// property, for primitive-element collections (which take the <c>array&lt;T&gt;</c>
/// direct path), and for unsupported element shapes (rejected via CG050).
/// </param>
/// <param name="InlineConstruction">
/// How Hydrate reconstructs an inline-collection element — see
/// <see cref="InlineConstructionKind"/>. <see cref="InlineConstructionKind.None"/>
/// whenever <paramref name="InlineMembers"/> is empty.
/// </param>
public sealed record PropertyModel(
    string Name,
    TypeRef Type,
    PropertyKind Kinds,
    RelationRole RelationRole,
    string? RelationKindFullName,
    ReferenceDeletePolicy ReferenceDelete,
    bool HasExplicitDeleteBehavior,
    bool HasMultipleDeleteBehaviors,
    bool HasGetter,
    bool HasSetter,
    bool HasInitOnlySetter,
    bool IsPartial,
    bool IsStatic,
    string DeclaredAccessibility,
    EquatableArray<InlineMember> InlineMembers,
    InlineConstructionKind InlineConstruction,
    EquatableArray<IndexAnnotationModel> Indexes,
    bool IsInline,
    AutoValueKind AutoValue = AutoValueKind.None);
