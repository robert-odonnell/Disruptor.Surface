using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Pipeline;

internal static class Diagnostics
{
    private const string Category = "Disruptor.Surface";

    public static readonly DiagnosticDescriptor TableMustBePartial = new(
        id: "CG001",
        title: "Table classes must be partial",
        messageFormat: "'{0}' is annotated with [Table] but is not declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TableHasMultipleIds = new(
        id: "CG008",
        title: "Table has more than one [Id] property",
        messageFormat: "[Table] '{0}' declares {1} [Id] properties; at most one is allowed",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChildrenElementMustBeConcrete = new(
        id: "CG009",
        title: "[Children] element type cannot be a generic type parameter",
        messageFormat: "[Children] property '{0}.{1}' has element type '{2}' which is a generic type parameter; use a named type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityInMultipleAggregates = new(
        id: "CG011",
        title: "Entity reachable from multiple aggregate roots",
        messageFormat: "Entity '{0}' is reachable via [Children] from multiple [AggregateRoot] entities ({1}). Each entity may belong to at most one aggregate.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReferenceMustTargetTable = new(
        id: "CG010",
        title: "[Reference] target must be a [Table] type",
        messageFormat: "[Reference] property '{0}.{1}' targets '{2}' which is not annotated with [Table]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsetRequiresNullable = new(
        id: "CG012",
        title: "[Unset] requires a nullable reference",
        messageFormat: "[Reference, Unset] on '{0}.{1}' requires nullable storage (T?); non-nullable references can't be unset safely",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MultipleDeleteBehaviors = new(
        id: "CG013",
        title: "Multiple reference-delete behaviors",
        messageFormat: "[Reference] property '{0}.{1}' declares more than one of [Reject]/[Unset]/[Cascade]/[Ignore]; only one delete behavior is allowed per reference",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CascadeCycle = new(
        id: "CG014",
        title: "Cascade-only reference cycle",
        messageFormat: "[Reference, Cascade] forms a cycle ({0}); break it by changing at least one edge to [Reject], [Unset], or [Ignore]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DeleteBehaviorOnParent = new(
        id: "CG015",
        title: "Delete behavior attribute on [Parent]",
        messageFormat: "[Parent] property '{0}.{1}' carries a delete-behavior attribute; parent deletion uses structural containment, not reference-delete behavior",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IgnoreDanglingWarning = new(
        id: "CG017",
        title: "[Ignore] may produce a dangling reference",
        messageFormat: "[Reference, Ignore] on '{0}.{1}' targets known table '{2}'; the reference is left unchanged on target deletion and may dangle",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MultipleCompositionRoots = new(
        id: "CG018",
        title: "Multiple [CompositionRoot] classes",
        messageFormat: "More than one [CompositionRoot] class declared ({0}); exactly one is allowed per compilation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CompositionRootMustBePartial = new(
        id: "CG019",
        title: "[CompositionRoot] class must be partial",
        messageFormat: "'{0}' is annotated with [CompositionRoot] but is not declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChildMissingParentPath = new(
        id: "CG020",
        title: "[Children] member requires a [Parent] path back to the aggregate root",
        messageFormat: "'{0}' is reachable from aggregate root '{1}' via [Children] but does not declare a [Parent] property linking back into the chain. Add a [Parent] {1} or [Parent] {{intermediate}} property so the loader can scope the row by parent path.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReferenceCrossesAggregate = new(
        id: "CG021",
        title: "[Reference] target must be in the same aggregate (or in no aggregate)",
        messageFormat: "[Reference] property '{0}.{1}' targets '{2}', which belongs to aggregate '{3}' — different from the owner's aggregate '{4}'. Cross-aggregate links should be expressed as a relation kind (forward/inverse attribute pair) instead. Same-aggregate references and references to shared records (no aggregate) are fine.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AnnotatedMemberMustBePartial = new(
        id: "CG022",
        title: "Annotated property must be declared partial",
        messageFormat: "'{0}.{1}' carries [{2}] but is not declared partial; the generator emits the implementation, so the user-side declaration must use the partial keyword",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingRoleAttributes = new(
        id: "CG024",
        title: "Property has multiple role attributes",
        messageFormat: "'{0}.{1}' carries multiple role attributes ({2}); a property's role IS its emit shape, and the five role attributes ([Id]/[Property]/[Parent]/[Children]/[Reference]) are mutually exclusive — pick one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyTypeNotMappable = new(
        id: "CG025",
        title: "[Property] type has no SurrealDB scalar mapping",
        messageFormat: "[Property] '{0}.{1}' has type '{2}' which has no SurrealDB scalar mapping; the schema would omit the field and reads/writes would fail at the database. Map the type to one of: string, int/long, bool, float/double, decimal, DateTime/DateTimeOffset, Guid, Ulid — or mark this as a [Reference]/[Children] if it's a record.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChildrenElementMustBeTable = new(
        id: "CG026",
        title: "[Children] element type must be a [Table]",
        messageFormat: "[Children] property '{0}.{1}' uses element type '{2}' which is not a [Table] class; the generated <c>QueryChildren&lt;T&gt;</c> body requires <c>T : IEntity, new()</c>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ParentMustTargetTable = new(
        id: "CG027",
        title: "[Parent] target must be a [Table]",
        messageFormat: "[Parent] property '{0}.{1}' targets type '{2}' which is not a [Table] class; parent links must point at another aggregate-graph entity",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AnnotatedMemberMustNotBeStatic = new(
        id: "CG028",
        title: "Annotated property must not be static",
        messageFormat: "'{0}.{1}' carries [{2}] but is declared static; annotations only apply to instance members — the emitted backing field, session-binding, and identity-map plumbing are all per-instance",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VariantMustBePartial = new(
        id: "CG029",
        title: "Relation variant classes must be partial",
        messageFormat: "'{0}' is annotated with a relation kind attribute (e.g. [Restricts]) on the class itself but is not declared partial; the generator emits the implementation half (IEntity scaffolding, Hydrate, SaveAsync), so the user-side declaration must use the partial keyword.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VariantEndpointPairCollision = new(
        id: "CG030",
        title: "Two relation variants share the same (In, Out) endpoint pair",
        messageFormat: "Relation kind '{0}' has multiple variants with the same ([In] type, [Out] type) pair ({1}); the hydration dispatcher discriminates variants by (in.tb, out.tb), so duplicate endpoint pairs would be ambiguous",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnionEndpointKindMismatch = new(
        id: "CG031",
        title: "Union endpoint is pinned to a different relation kind than the variant declares",
        messageFormat: "Variant '{0}' carries [{1}] but its [{2}] endpoint property is typed to union interface '{3}' which is pinned to kind '{4}'; the union's In<TKind> / Out<TKind> base parameter must match the variant's kind",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DeadUnionEndpoint = new(
        id: "CG032",
        title: "Union endpoint interface has no member tables",
        messageFormat: "Union endpoint interface '{0}' is attributed for kind '{1}' but no per-table marker (partial interface I{{Name}}RecordId : {0}) enrols any [Table] in the union; the union is unreachable",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SharedShapeMustBePartial = new(
        id: "CG033",
        title: "Shared-shape relation interface must be partial",
        messageFormat: "Interface '{0}' derives from IRelationVariant and is treated as a shared-shape contract; declare it partial so the generator can emit the static Create<TKind> factory fragment onto it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SharedShapeHasNoVariants = new(
        id: "CG035",
        title: "Shared-shape relation interface has no implementing variants",
        messageFormat: "Interface '{0}' derives from IRelationVariant but no relation variant class lists it as a base; the generated Create<TKind> factory would have nothing to dispatch to",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SharedShapeLiftConflict = new(
        id: "CG036",
        title: "Shared-shape lift conflict",
        messageFormat: "Relation variant '{0}' cannot lift shared-shape interface '{1}' because lifted member '{2}' conflicts with existing member '{3}' ({4} vs {5})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IndexedFieldUnsupported = new(
        id: "CG037",
        title: "Indexed field is not a persisted entity field",
        messageFormat: "Index '{1}' on table '{0}' includes unsupported member '{2}': {3}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CompositeIndexSplitAcrossPartials = new(
        id: "CG038",
        title: "Composite index fields must be declared together",
        messageFormat: "Index '{1}' on table '{0}' is composite but its fields are split across partial declarations; {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UniqueIndexRequiresNonNullable = new(
        id: "CG039",
        title: "Unique index fields must be non-nullable",
        messageFormat: "Unique index '{1}' on table '{0}' includes nullable member '{2}'; {3}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IndexNameCollision = new(
        id: "CG040",
        title: "Index schema name collision",
        messageFormat: "Table '{0}' has multiple indexes that resolve to schema name '{1}' ({2}); rename one of the index attribute types",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateIndexedField = new(
        id: "CG041",
        title: "Index attribute applied more than once to the same field",
        messageFormat: "Index '{1}' on table '{0}' includes member '{2}' more than once; {3}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TableNameCollision = new(
        id: "CG042",
        title: "Multiple [Table] classes map to the same SurrealDB table name",
        messageFormat: "[Table] classes {0} map to the same SurrealDB table name '{1}'. Table names are pluralised + snake-cased from the class's simple name, so these distinct CLR types would silently share one physical table (and the generated query/hydration accessors would collide). Rename one of the classes.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EdgeNameCollision = new(
        id: "CG043",
        title: "Multiple relation kinds map to the same SurrealDB edge table name",
        messageFormat: "Relation kind attributes {0} map to the same SurrealDB edge table name '{1}'. Edge names are snake-cased from the attribute's simple class name (minus the Attribute suffix), so these distinct kinds would silently share one edge table with merged FROM/TO clauses. Rename one of the attribute classes.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AggregateRootNameCollision = new(
        id: "CG044",
        title: "Multiple [AggregateRoot] tables share a simple name",
        messageFormat: "[AggregateRoot] tables {0} share the simple name '{1}'. The generated aggregate loader class, Load{1}Async entry points, and LoadAsync query extensions are all keyed on the root's simple name and would collide. Rename one of the classes.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedModelType = new(
        id: "CG045",
        title: "Model class must not be nested inside another type",
        messageFormat: "'{0}' is annotated with [{1}] but is nested inside another type; the generator emits namespace-scoped partials only, so a nested declaration cannot receive its implementation half. Move the class to namespace scope.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VariantDuplicateRole = new(
        id: "CG046",
        title: "Relation variant declares duplicate role annotations",
        messageFormat: "Relation variant '{0}' declares more than one [{1}] member; the [In]/[Out]/[Id] roles are singular per variant (one source endpoint, one target endpoint, one id), so duplicates are ambiguous — remove the extra [{1}] annotation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VariantMissingEndpoints = new(
        id: "CG047",
        title: "Relation variant declares or inherits no [In]/[Out] endpoints",
        messageFormat: "Relation variant '{0}' declares or inherits no {1}; every variant needs exactly one [In] and one [Out] endpoint — declare them on the class, or implement an annotated shared-shape interface (an IRelationVariant-derived interface whose members carry [In]/[Out]) that supplies them",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
