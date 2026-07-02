namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One scalar/reference property on a relation variant class — name, snake-cased SurrealDB
/// field name, type info, role (In / Out / Id / Property), and (for In/Out only) the
/// reference-delete policy. Sibling of <see cref="PropertyModel"/> for entity tables; kept
/// separate so the variant emitter doesn't drag along entity-only fields (Children,
/// Parent's two backing fields, etc.).
/// </summary>
public sealed record RelationVariantPropertyModel(
    string Name,
    string FieldName,
    TypeRef Type,
    RelationVariantPropertyRole Role,
    ReferenceDeletePolicy DeletePolicy,
    bool IsPartial,
    bool HasSetter,
    bool HasInitOnlySetter,
    bool IsStatic,
    string DeclaredAccessibility);

/// <summary>Which annotation drove the property into the variant model.</summary>
public enum RelationVariantPropertyRole
{
    None,
    Id,
    In,
    Out,
    Property,
}

/// <summary>
/// One relation variant class — a class annotated with a <c>ForwardRelation</c>- or
/// <c>InverseRelation&lt;T&gt;</c>-derived attribute (e.g. <c>[Restricts]</c> /
/// <c>[RestrictedBy]</c>). Carries the kind it belongs to, the In and Out endpoint
/// declarations, optional explicit Id, and any payload [Property] members.
/// </summary>
/// <param name="FullName">Fully-qualified type name without the leading <c>global::</c>.</param>
/// <param name="Namespace">Namespace, or empty string for global namespace.</param>
/// <param name="Name">Short type name.</param>
/// <param name="KindAttributeFqn">FQN of the relation attribute applied (e.g. <c>Disruptor.Surface.Sample.Relations.RestrictsAttribute</c>).</param>
/// <param name="In">Snapshot of the <c>[In]</c>-annotated property. <c>null</c> at extraction time when the variant carries zero own annotated members and is awaiting an interface lift (preview.56); the linker fills this in from a matching annotated shared-shape interface candidate before emit, and the emitter drops any variant where <see cref="In"/> stays null.</param>
/// <param name="Out">Snapshot of the <c>[Out]</c>-annotated property. Same lift semantics as <see cref="In"/>.</param>
/// <param name="Id">Optional <c>[Id]</c>-annotated property. <c>null</c> when absent (generator emits the internal id anchor either way).</param>
/// <param name="PayloadProperties">Zero or more <c>[Property]</c>-annotated members carrying edge payload.</param>
/// <param name="IsPartial">Whether the class itself is declared <c>partial</c> (required to receive emitted IEntity members).</param>
/// <param name="DeclaredAccessibility">Class-level accessibility — emitted IEntity members match.</param>
/// <param name="ImplementedInterfaceFullNames">User-declared interfaces the variant implements (FQNs without <c>global::</c>). Used to match the variant into <see cref="SharedShapeModel"/>s — interfaces deriving from <c>Disruptor.Surface.Runtime.IRelationVariant</c> are shared-shape contracts.</param>
/// <param name="DuplicateRoles">Roles ("In"/"Out"/"Id") that the class declares more than once, each carrying the location of the duplicate role attribute. Non-empty variants are malformed: <c>RelationLinker.Build</c> filters them into <see cref="RelationVariantIssueModel"/>s and the diagnostics output reports CG046 — they never reach the emitters (so the embedded <see cref="LocationInfo"/> only exists on an already-failing build). For duplicated roles the single-slot snapshot (<see cref="In"/>/<see cref="Out"/>/<see cref="Id"/>) holds the last declaration seen; the value is never emitted.</param>
/// <param name="IssueLocation">The declaration's identifier location, captured ONLY when the extractor already knows the variant will be rejected (record declaration — CG048). MUST stay <c>null</c> on healthy variants; see <see cref="TableModel.IssueLocation"/> for the caching rationale.</param>
public sealed record RelationVariantModel(
    string FullName,
    string Namespace,
    string Name,
    string KindAttributeFqn,
    RelationVariantPropertyModel? In,
    RelationVariantPropertyModel? Out,
    RelationVariantPropertyModel? Id,
    EquatableArray<RelationVariantPropertyModel> PayloadProperties,
    bool IsPartial,
    string DeclaredAccessibility,
    EquatableArray<string> ImplementedInterfaceFullNames,
    EquatableArray<DuplicateRoleModel> DuplicateRoles,
    bool IsRecord,
    LocationInfo? IssueLocation);

/// <summary>
/// One duplicated singular role on a relation variant ("In"/"Out"/"Id") plus the
/// location of the duplicate role attribute application — CG046 points there rather
/// than at the class. Only exists on malformed (already-failing) variants, so the
/// position-sensitive <see cref="LocationInfo"/> cannot regress incremental caching.
/// </summary>
public sealed record DuplicateRoleModel(
    string Role,
    LocationInfo? Location);
