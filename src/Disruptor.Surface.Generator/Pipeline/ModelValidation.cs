using System.Collections.Immutable;
using System.Globalization;
using Disruptor.Surface.Generator.Emit;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// One CG diagnostic computed from the linked <see cref="ModelGraph"/>, before location
/// resolution. Value-equatable and — crucially — position-independent on the healthy
/// path: <paramref name="TypeKey"/> / <paramref name="MemberName"/> are symbolic keys
/// resolved against the declaration-location map at report time, and
/// <paramref name="ExactLocation"/> is only ever populated from issue models (which
/// exist only on already-failing builds).
/// </summary>
/// <param name="Descriptor">The CGxxx descriptor (static singleton from <see cref="Diagnostics"/>).</param>
/// <param name="MessageArgs">Pre-formatted (invariant-culture) message arguments.</param>
/// <param name="TypeKey">Full name (NormaliseFullName format) of the declaration to point at.</param>
/// <param name="MemberName">Optional property name within <paramref name="TypeKey"/> for member-level precision.</param>
/// <param name="ExactLocation">Exact location captured at extraction (issue models only); wins over the map lookup.</param>
public sealed record PendingDiagnostic(
    DiagnosticDescriptor Descriptor,
    EquatableArray<string> MessageArgs,
    string? TypeKey,
    string? MemberName,
    LocationInfo? ExactLocation);

/// <summary>
/// A <see cref="PendingDiagnostic"/> after location resolution — the shape the
/// diagnostics-only <c>RegisterSourceOutput</c> consumes. Kept value-equatable so that
/// when a position-only edit changes the location map but the compilation has no
/// diagnostics, the resolved (empty) set compares equal and the report output stays
/// cached.
/// </summary>
public sealed record LocatedDiagnostic(
    DiagnosticDescriptor Descriptor,
    EquatableArray<string> MessageArgs,
    LocationInfo? Location);

/// <summary>
/// The full validation verdict for one <see cref="ModelGraph"/>. Only
/// <see cref="Diagnostics"/> flows through the incremental pipeline (it must be — and is
/// — value-equatable); the flags/sets are consumed synchronously by
/// <c>ModelGenerator.Emit</c> to keep the fail-closed emitter-skip behavior that used to
/// be interleaved with diagnostic reporting.
/// </summary>
internal sealed class ValidationResult
{
    public required EquatableArray<PendingDiagnostic> Diagnostics { get; init; }
    public required bool CompositionRootValid { get; init; }
    public required bool LoaderValid { get; init; }

    /// <summary>Tables that skip every per-table emitter (CG001 not partial / CG008 multiple ids).</summary>
    public required HashSet<string> SkippedTables { get; init; }

    /// <summary>Tables that keep the id/query emitters but skip <c>PartialEmitter</c> (any per-table validation error).</summary>
    public required HashSet<string> PartialSuppressedTables { get; init; }
}

/// <summary>
/// Pure validation over the linked <see cref="ModelGraph"/> — every CG diagnostic that
/// used to be reported inline from <c>ModelGenerator.Emit</c> is computed here as a
/// <see cref="PendingDiagnostic"/>. The split exists for incremental-caching reasons:
/// the emit output must stay position-independent, so locations are attached in a
/// separate pipeline node (<see cref="Locate"/>) that combines these pending diagnostics
/// with the syntax-only declaration-location map and feeds a dedicated diagnostics
/// output. See the "Diagnostic source locations" section in <c>docs/architecture.md</c>.
/// </summary>
internal static class ModelValidation
{
    public static ValidationResult Validate(ModelGraph graph)
    {
        var pending = ImmutableArray.CreateBuilder<PendingDiagnostic>();

        // CG042/CG043/CG044 — pre-emit uniqueness violations. Names both (all)
        // colliding types and the generated name they collide on. Emission stays
        // fail-closed alongside: the emitters that key output on the colliding name
        // skip the non-first participants (ModelGraph.IsCollisionLoser) so the build
        // fails with the CG error instead of a duplicate-hint CS8785 or a CS0102 wall.
        // The diagnostic points at the first participant's declaration (map-resolved).
        foreach (var collision in graph.NameCollisions)
        {
            var descriptor = collision.Kind switch
            {
                NameCollisionKind.TableName => Diagnostics.TableNameCollision,
                NameCollisionKind.EdgeName => Diagnostics.EdgeNameCollision,
                _ => Diagnostics.AggregateRootNameCollision,
            };
            Add(pending, descriptor,
                typeKey: collision.ParticipantFullNames.Count > 0 ? collision.ParticipantFullNames[0] : null,
                args: [string.Join(" and ", collision.ParticipantFullNames.Select(n => $"'{n}'")), collision.CollidingName]);
        }

        // CG045 — nested [Table] / [CompositionRoot]. The linker already pulled these
        // out of the graph, so nothing is emitted for them; this explains why. The
        // location was captured at extraction (issue model — already-failing build).
        foreach (var nested in graph.NestedTypeIssues)
        {
            Add(pending, Diagnostics.NestedModelType,
                typeKey: nested.FullName,
                exactLocation: nested.Location,
                args: [nested.FullName, nested.AttributeName]);
        }

        // CG048 — [Table] / [CompositionRoot] / relation kind attributes on record
        // declarations. The linker pulled these out of the graph; this explains why
        // nothing was emitted for them (previously: silent skip, clean compile).
        foreach (var record in graph.RecordTypeIssues)
        {
            Add(pending, Diagnostics.RecordTypeNotSupported,
                typeKey: record.FullName,
                exactLocation: record.Location,
                args: [record.FullName, record.AttributeName]);
        }

        // CG049 — generic [Table] classes, rejected fail-closed by the linker (the
        // table name ignores type arguments, so closed constructions would silently
        // share one physical table; the generated roots can't name an open generic).
        foreach (var generic in graph.GenericTableIssues)
        {
            Add(pending, Diagnostics.GenericTableNotSupported,
                typeKey: generic.FullName,
                exactLocation: generic.Location,
                args: [generic.FullName, generic.TypeParameters, generic.SimpleName]);
        }

        // CG046/CG047 — malformed relation variants pulled out of the graph by the
        // linker (duplicate [In]/[Out]/[Id] roles; endpoints unresolved after the
        // shared-shape lift). CG046 carries the duplicate role attribute's location
        // captured at extraction; CG047 falls back to the variant declaration via the map.
        foreach (var issue in graph.RelationVariantIssues)
        {
            var descriptor = issue.Kind switch
            {
                RelationVariantIssueKind.DuplicateRole => Diagnostics.VariantDuplicateRole,
                _ => Diagnostics.VariantMissingEndpoints,
            };
            Add(pending, descriptor,
                typeKey: issue.FullName,
                exactLocation: issue.Location,
                args: [issue.FullName, issue.Detail]);
        }

        foreach (var conflict in graph.AggregateConflicts)
        {
            // Format from RelationLinker.ComputeAggregates: "Member|Root1,Root2,...".
            var pipe = conflict.IndexOf('|');
            var member = pipe < 0 ? conflict : conflict.Substring(0, pipe);
            var roots = pipe < 0 ? string.Empty : conflict.Substring(pipe + 1);
            Add(pending, Diagnostics.EntityInMultipleAggregates,
                typeKey: member,
                args: [member, roots]);
        }

        // CG014 — cascade cycles among [Reference, Cascade] edges. Points at the first
        // table in the cycle path.
        foreach (var cycle in graph.CascadeCycles)
        {
            Add(pending, Diagnostics.CascadeCycle,
                typeKey: FirstCycleNode(cycle),
                args: [cycle]);
        }

        // Per-table per-property reference-delete diagnostics.
        var tableLookup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in graph.Tables)
        {
            tableLookup.Add(t.FullName);
        }

        foreach (var table in graph.Tables)
        {
            foreach (var p in table.Properties)
            {
                // CG015 — delete behavior attribute on [Parent].
                if (p.Kinds.HasFlag(PropertyKind.Parent) && p.HasExplicitDeleteBehavior)
                {
                    Add(pending, Diagnostics.DeleteBehaviorOnParent,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name]);
                }

                if (!p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    continue;
                }

                // CG013 — multiple delete behaviors on a single [Reference].
                if (p.HasMultipleDeleteBehaviors)
                {
                    Add(pending, Diagnostics.MultipleDeleteBehaviors,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name]);
                }

                // CG012 — [Unset] requires nullable storage.
                if (p.ReferenceDelete == ReferenceDeletePolicy.Unset && !p.Type.IsNullable)
                {
                    Add(pending, Diagnostics.UnsetRequiresNullable,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name]);
                }

                // CG017 — [Ignore] to a known table is dangling-prone (warning).
                if (p.ReferenceDelete == ReferenceDeletePolicy.Ignore && p.Type.IsTableType)
                {
                    var targetFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                    if (tableLookup.Contains(targetFqn))
                    {
                        Add(pending, Diagnostics.IgnoreDanglingWarning,
                            typeKey: table.FullName, memberName: p.Name,
                            args: [table.FullName, p.Name, targetFqn]);
                    }
                }

                // CG021 — [Reference] target must be in the same aggregate as the owner
                // (or in no aggregate at all, like a shared sidecar). Cross-aggregate
                // links go through forward/inverse relation kinds, not entity references.
                if (p.Type.IsTableType)
                {
                    var targetFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                    var ownerAggregate = graph.AggregateRootOf(table.FullName);
                    var targetAggregate = graph.AggregateRootOf(targetFqn);
                    if (ownerAggregate is not null
                        && targetAggregate is not null
                        && !string.Equals(ownerAggregate, targetAggregate, StringComparison.Ordinal))
                    {
                        Add(pending, Diagnostics.ReferenceCrossesAggregate,
                            typeKey: table.FullName, memberName: p.Name,
                            args: [table.FullName, p.Name, targetFqn, targetAggregate, ownerAggregate]);
                    }
                }
            }
        }

        // CG032 — union endpoint interface with zero enrolled tables. Warning (not
        // error) because the union still resolves as a type and won't break compilation
        // — but variants typing an endpoint to it can never satisfy a substrate FROM/TO
        // clause.
        foreach (var unionEndpoint in graph.UnionEndpoints)
        {
            if (unionEndpoint.MemberTableFullNames.Count > 0)
            {
                continue;
            }

            Add(pending, Diagnostics.DeadUnionEndpoint,
                typeKey: unionEndpoint.InterfaceFullName,
                args: [unionEndpoint.InterfaceFullName, unionEndpoint.KindFullName]);
        }

        // CG018 — more than one [CompositionRoot] in the compilation. CG019 — the one
        // that's there isn't partial. Either case skips the emitter (avoids dragging
        // half-broken Load{Root}Async methods into the consumer compilation).
        var compositionRootValid = graph.CompositionRoots.Count <= 1;
        if (graph.CompositionRoots.Count > 1)
        {
            var names = string.Join(", ", graph.CompositionRoots.Select(c => c.FullName));
            Add(pending, Diagnostics.MultipleCompositionRoots,
                typeKey: graph.CompositionRoots[0].FullName,
                args: [names]);
        }

        if (graph.CompositionRoots.Count == 1 && !graph.CompositionRoots[0].IsPartial)
        {
            Add(pending, Diagnostics.CompositionRootMustBePartial,
                typeKey: graph.CompositionRoots[0].FullName,
                args: [graph.CompositionRoots[0].FullName]);
            compositionRootValid = false;
        }

        // CG020 — every member of an aggregate must be reachable from the root via
        // [Parent] links so the loader's dotted-path WHERE clauses can scope each row
        // by parent. Aggregate membership is decided by [Children] reachability, so a
        // mismatch (Children says yes, [Parent] BFS says no) leaves the member silently
        // unloadable.
        var loaderValid = true;
        var byFullName = graph.Tables.ToDictionary(t => t.FullName);
        foreach (var agg in graph.Aggregates)
        {
            var reachable = ReachableViaParentLinks(agg, byFullName);
            foreach (var memberFullName in agg.MemberFullNames)
            {
                if (!reachable.Contains(memberFullName))
                {
                    Add(pending, Diagnostics.ChildMissingParentPath,
                        typeKey: memberFullName,
                        args: [memberFullName, agg.RootFullName]);
                    loaderValid = false;
                }
            }
        }

        foreach (var issue in graph.IndexIssues)
        {
            AddIndexIssue(pending, issue);
        }

        var skippedTables = new HashSet<string>(StringComparer.Ordinal);
        var partialSuppressedTables = new HashSet<string>(StringComparer.Ordinal);

        foreach (var table in graph.Tables)
        {
            if (!table.IsPartial)
            {
                Add(pending, Diagnostics.TableMustBePartial,
                    typeKey: table.FullName,
                    args: [table.FullName]);
                skippedTables.Add(table.FullName);
                continue;
            }

            // CG008 — at most one [Id] property (the user's optional public-facing accessor).
            // [Id] is no longer required: the generator always emits the internal id anchor
            // and the IEntity.Id accessor on every [Table]; [Id] just opts the user into a
            // public partial property that delegates to the anchor.
            var idCount = table.Properties.Count(p => p.Kinds.HasFlag(PropertyKind.Id));
            if (idCount > 1)
            {
                Add(pending, Diagnostics.TableHasMultipleIds,
                    typeKey: table.FullName,
                    args: [table.FullName, Invariant(idCount)]);
                skippedTables.Add(table.FullName);
                continue;
            }

            var valid = true;

            // CG022 — every annotated property must be declared partial. Without it, the
            // generator can't emit the implementation half (backing field, getter/setter,
            // hydrate body all live in the partial fragment), so a non-partial member
            // tagged [Property]/[Reference]/[Parent]/[Children]/[Id] would produce
            // generated code referencing storage that was never emitted (CS0103). Catch
            // it here so the diagnostic explains the cause; without it the user gets a
            // confusing CS0103 in a .g.cs file they didn't write.
            foreach (var p in table.Properties)
            {
                if (p.IsPartial)
                {
                    continue;
                }

                var attrName = AnnotationLabel(p);
                if (attrName is null)
                {
                    continue;
                }

                Add(pending, Diagnostics.AnnotatedMemberMustBePartial,
                    typeKey: table.FullName, memberName: p.Name,
                    args: [table.FullName, p.Name, attrName]);
                valid = false;
            }

            // CG024 — at most one role attribute per property. The five role attributes
            // ([Id]/[Property]/[Parent]/[Children]/[Reference]) each select a distinct
            // emit shape — scalar field, structural parent link, child collection, record
            // reference, identity. Mixing two means the emitters silently disagree:
            // PartialEmitter prioritises [Property] over [Reference] while SchemaEmitter
            // prioritises [Reference] over [Property], so [Property][Reference] yields a
            // scalar CLR setter writing into a record<>-typed schema column. Reject the
            // combo at the model boundary instead of letting it ship.
            foreach (var p in table.Properties)
            {
                var roleNames = new List<string>();
                if (p.Kinds.HasFlag(PropertyKind.Id))
                {
                    roleNames.Add("Id");
                }

                if (p.Kinds.HasFlag(PropertyKind.Property))
                {
                    roleNames.Add("Property");
                }

                if (p.Kinds.HasFlag(PropertyKind.Parent))
                {
                    roleNames.Add("Parent");
                }

                if (p.Kinds.HasFlag(PropertyKind.Children))
                {
                    roleNames.Add("Children");
                }

                if (p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    roleNames.Add("Reference");
                }

                if (roleNames.Count <= 1)
                {
                    continue;
                }

                Add(pending, Diagnostics.ConflictingRoleAttributes,
                    typeKey: table.FullName, memberName: p.Name,
                    args: [table.FullName, p.Name, string.Join(" + ", roleNames)]);
                valid = false;
            }

            foreach (var (memberName, memberType) in EnumerateReadSideTypes(table, PropertyKind.Children))
            {
                var content = UnwrapTask(memberType);
                var element = content.ElementType ?? content;
                if (element.IsTypeParameter)
                {
                    // CG009 — type-parameter element. The child's concrete type isn't
                    // known at codegen time, so the loader can't pick the row-hydrator.
                    Add(pending, Diagnostics.ChildrenElementMustBeConcrete,
                        typeKey: table.FullName, memberName: memberName,
                        args: [table.FullName, memberName, element.DisplayName]);
                    valid = false;
                }
                else if (!element.IsTableType)
                {
                    // CG026 — concrete-but-not-a-Table element (e.g. IReadOnlyCollection<string>).
                    // The emitted Session.QueryChildren<T>(...) call has `where T : IEntity, new()`,
                    // so this would surface as a generic-constraint CS error in generated code
                    // without the diagnostic.
                    Add(pending, Diagnostics.ChildrenElementMustBeTable,
                        typeKey: table.FullName, memberName: memberName,
                        args: [table.FullName, memberName, element.DisplayName]);
                    valid = false;
                }
            }

            foreach (var (memberName, memberType) in EnumerateReadSideTypes(table, PropertyKind.Reference))
            {
                var target = UnwrapTask(memberType);
                if (target.IsTableType)
                {
                    continue;
                }

                Add(pending, Diagnostics.ReferenceMustTargetTable,
                    typeKey: table.FullName, memberName: memberName,
                    args: [table.FullName, memberName, memberType.DisplayName]);
                valid = false;
            }

            // CG027 — [Parent] target must be a [Table]. Same family as CG010 / CG026
            // (generic constraint violation in emitted code without the diagnostic).
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Parent))
                {
                    continue;
                }

                if (p.Type.IsTableType)
                {
                    continue;
                }

                Add(pending, Diagnostics.ParentMustTargetTable,
                    typeKey: table.FullName, memberName: p.Name,
                    args: [table.FullName, p.Name, p.Type.DisplayName]);
                valid = false;
            }

            // CG028 — annotated property must not be static. Every emit shape (backing
            // field, _session reference, identity-map plumbing, hydrate body) is
            // per-instance; a static partial property would compile to `static partial T
            // Foo` and the generator's emitted instance backing field wouldn't match.
            foreach (var p in table.Properties)
            {
                if (!p.IsStatic)
                {
                    continue;
                }

                var attrName = AnnotationLabel(p);
                if (attrName is null)
                {
                    continue;
                }

                Add(pending, Diagnostics.AnnotatedMemberMustNotBeStatic,
                    typeKey: table.FullName, memberName: p.Name,
                    args: [table.FullName, p.Name, attrName]);
                valid = false;
            }

            // CG025 — [Property] type must map to a SurrealDB scalar. Unmapped types
            // (Uri, TimeSpan, custom value objects, …) compile fine on the CLR side but
            // SchemaEmitter would silently omit the field, so reads/writes would fail at
            // the database, not at build time. Skip element-collection [Property] shapes
            // (List<T> / IList<T> / IReadOnlyList<T> — handled separately in SchemaEmitter
            // via the array<object> + sub-field path) and skip relation role overlay
            // (they don't take the scalar-emission code path).
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Property))
                {
                    continue;
                }

                if (p.RelationRole != RelationRole.None)
                {
                    continue;
                }

                if (IsElementCollection(p.Type))
                {
                    continue;
                }

                if (SchemaEmitter.IsMappableScalar(p.Type))
                {
                    continue;
                }

                Add(pending, Diagnostics.PropertyTypeNotMappable,
                    typeKey: table.FullName, memberName: p.Name,
                    args: [table.FullName, p.Name, p.Type.FullyQualifiedName]);
                valid = false;
            }

            // CG052–CG055 — [CreatedAt]/[UpdatedAt]/[Version] marker validation. The
            // markers overlay a scalar [Property] (the field reuses the standard schema/
            // hydrate/save paths); the emitted SaveAsync additionally writes the backing
            // field itself, so the shape is part of the contract: exactly one marker per
            // property, combined with [Property], typed to what the library writes
            // (datetime for the audit pair; a non-nullable integer counter for version).
            foreach (var p in table.Properties)
            {
                if (p.AutoValue == AutoValueKind.None)
                {
                    continue;
                }

                // CG055 — mutually exclusive markers on one property. Each marker
                // prescribes a different write behavior for the same field.
                var markerNames = AutoValueLabels(p.AutoValue);
                if (markerNames.Count > 1)
                {
                    Add(pending, Diagnostics.AutoValueMarkersConflict,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name, string.Join(" + ", markerNames)]);
                    valid = false;
                    continue;
                }

                var marker = markerNames[0];
                var expectedType = p.AutoValue == AutoValueKind.Version
                    ? "a non-nullable int or long"
                    : "a non-nullable System.DateTime or System.DateTimeOffset";

                // CG052 — must be a scalar [Property] field (not [Reference]/[Parent]/…,
                // not a relation read collection, not an element collection).
                if (p.Kinds != PropertyKind.Property
                    || p.RelationRole != RelationRole.None
                    || IsElementCollection(p.Type))
                {
                    Add(pending, Diagnostics.AutoValueRequiresProperty,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name, marker, p.AutoValue == AutoValueKind.Version ? "int" : "System.DateTimeOffset"]);
                    valid = false;
                    continue;
                }

                // CG053 — the library writes the field, so the type is fixed.
                if (!IsValidAutoValueType(p))
                {
                    Add(pending, Diagnostics.AutoValueTypeInvalid,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name, marker, expectedType, p.Type.FullyQualifiedName]);
                    valid = false;
                }
            }

            // CG054 — at most one property per marker per table: the emitted SaveAsync
            // stamps exactly one created field, one updated field, one version counter.
            foreach (var kind in new[] { AutoValueKind.CreatedAt, AutoValueKind.UpdatedAt, AutoValueKind.Version })
            {
                var count = table.Properties.Count(p => p.AutoValue.HasFlag(kind));
                if (count <= 1)
                {
                    continue;
                }

                Add(pending, Diagnostics.AutoValueDuplicatedOnTable,
                    typeKey: table.FullName,
                    args: [table.FullName, kind.ToString(), Invariant(count)]);
                valid = false;
            }

            // CG050/CG051 — element-collection [Property] shape validation. CG025 skips
            // these (they aren't scalars), so without their own checks unsupported
            // element types surfaced as raw CS errors inside the .g.cs: string elements
            // generated `new string(this[]: …, Length: …)` (CS0443), List<int> fell to
            // the scalar save path with no matching ContentValue.Set overload (CS1503),
            // and a user-declared setter met a getter-only emitted impl (CS9252).
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Property) || p.RelationRole != RelationRole.None)
                {
                    continue;
                }

                if (!IsElementCollection(p.Type))
                {
                    continue;
                }

                // CG051 — element collections are get-only by contract: the emitted impl
                // is a read-only view over a generated backing list plus Add/Remove/Clear
                // helpers. A setter (or init) has nothing to pair with.
                if (p.HasSetter || p.HasInitOnlySetter)
                {
                    Add(pending, Diagnostics.ElementCollectionMustBeGetOnly,
                        typeKey: table.FullName, memberName: p.Name,
                        args: [table.FullName, p.Name, SurrealNaming.Singularize(p.Name)]);
                    valid = false;
                }

                // Supported element shapes: (a) inline record/POCO — extraction resolved
                // a constructible member set; (b) non-nullable mappable scalar — the
                // array<T> direct path. Everything else is CG050.
                if (p.InlineMembers.Count > 0)
                {
                    continue;
                }

                var element = p.Type.TypeArguments.Count == 1 ? p.Type.TypeArguments[0] : null;
                if (element is not null && !element.IsNullable && SchemaEmitter.IsMappableScalar(element))
                {
                    continue;
                }

                var detail = element is null
                    ? "the element type could not be resolved at extraction time"
                    : element.IsNullable && SchemaEmitter.IsMappableScalar(element)
                        ? "nullable element types are not supported — a null element has no stable wire round-trip; use a non-nullable element type and omit unset values"
                        : "the element type is neither a SurrealDB-mappable scalar nor a type constructible from its public mappable properties (use a positional record, or a parameterless-constructor type whose mappable public properties are all readable and settable)";
                Add(pending, Diagnostics.ElementCollectionElementNotSupported,
                    typeKey: table.FullName, memberName: p.Name,
                    args: [table.FullName, p.Name, element?.FullyQualifiedName ?? "<unresolved>", detail]);
                valid = false;
            }

            if (!valid)
            {
                partialSuppressedTables.Add(table.FullName);
            }
        }

        // CG056 (warning) — relation-variant payload [Property] whose type has no
        // SurrealDB scalar mapping. The entity-table equivalent is CG025 (error), but
        // variant payloads stay a warning: multi-variant kinds get SCHEMALESS edge
        // tables where field omission is a tolerated shape, so the fail-soft contract
        // is "the property compiles as in-memory-only state" — SchemaEmitter omits the
        // DDL field and RelationVariantEmitter omits the field from the dispatched
        // content + hydrate.
        foreach (var variant in graph.RelationVariants)
        {
            foreach (var p in variant.PayloadProperties)
            {
                if (p.Role != RelationVariantPropertyRole.Property
                    || SchemaEmitter.IsMappableScalar(p.Type))
                {
                    continue;
                }

                Add(pending, Diagnostics.VariantPayloadTypeNotMappable,
                    typeKey: variant.FullName, memberName: p.Name,
                    args: [variant.FullName, p.Name, p.Type.FullyQualifiedName]);
            }
        }

        // CG036 — a relation variant attempted to lift annotated shared-shape members
        // from multiple sources, but an overlapping role/name/type/nullability contract
        // disagreed. The variant stays dropped; this diagnostic explains why.
        foreach (var conflict in graph.SharedShapeLiftConflicts)
        {
            Add(pending, Diagnostics.SharedShapeLiftConflict,
                typeKey: conflict.VariantFullName,
                args:
                [
                    conflict.VariantFullName,
                    conflict.InterfaceFullName,
                    conflict.LiftedName,
                    conflict.ExistingName,
                    DescribeSharedShapeLiftMember(
                        conflict.LiftedRole,
                        conflict.LiftedName,
                        conflict.LiftedTypeFullName,
                        conflict.LiftedNullable),
                    DescribeSharedShapeLiftMember(
                        conflict.ExistingRole,
                        conflict.ExistingName,
                        conflict.ExistingTypeFullName,
                        conflict.ExistingNullable),
                ]);
        }

        // CG033 — shared-shape interface must be declared partial to receive the
        // emitted static Create<TKind> factory fragment. CG035 — interface attributed
        // as a shared shape (derived from IRelationVariant) but no variant implements
        // it; warning, not error (the interface still functions as a marker type).
        foreach (var shape in graph.SharedShapes)
        {
            if (!shape.IsPartial)
            {
                Add(pending, Diagnostics.SharedShapeMustBePartial,
                    typeKey: shape.InterfaceFullName,
                    args: [shape.InterfaceFullName]);
            }

            if (shape.IsPartial && shape.Variants.Count == 0)
            {
                Add(pending, Diagnostics.SharedShapeHasNoVariants,
                    typeKey: shape.InterfaceFullName,
                    args: [shape.InterfaceFullName]);
            }
        }

        return new ValidationResult
        {
            Diagnostics = new EquatableArray<PendingDiagnostic>(pending.ToImmutable()),
            CompositionRootValid = compositionRootValid,
            LoaderValid = loaderValid,
            SkippedTables = skippedTables,
            PartialSuppressedTables = partialSuppressedTables,
        };
    }

    /// <summary>
    /// Resolves each pending diagnostic's symbolic key against the declaration-location
    /// map. Runs in its own pipeline node (position-sensitive by design — its map input
    /// changes on every edit); when the compilation has no diagnostics the resolved set
    /// is empty and value-equal to the previous run, so the diagnostics output itself
    /// stays cached.
    /// </summary>
    public static EquatableArray<LocatedDiagnostic> Locate(
        EquatableArray<PendingDiagnostic> pending,
        ImmutableArray<TypeDeclarationLocations> declarations)
    {
        if (pending.Count == 0)
        {
            return EquatableArray<LocatedDiagnostic>.Empty;
        }

        // First declaration per key wins — deterministic for partial types because the
        // collected provider preserves document order.
        var typeLocations = new Dictionary<string, LocationInfo>(StringComparer.Ordinal);
        var memberLocations = new Dictionary<(string TypeKey, string Member), LocationInfo>();
        foreach (var declaration in declarations)
        {
            if (!typeLocations.ContainsKey(declaration.TypeKey))
            {
                typeLocations[declaration.TypeKey] = declaration.Location;
            }

            foreach (var member in declaration.Members)
            {
                var key = (declaration.TypeKey, member.Name);
                if (!memberLocations.ContainsKey(key))
                {
                    memberLocations[key] = member.Location;
                }
            }
        }

        var located = ImmutableArray.CreateBuilder<LocatedDiagnostic>(pending.Count);
        foreach (var diagnostic in pending)
        {
            var location = diagnostic.ExactLocation;
            if (location is null && diagnostic.TypeKey is { } typeKey)
            {
                if (diagnostic.MemberName is { } member
                    && memberLocations.TryGetValue((typeKey, member), out var memberLocation))
                {
                    location = memberLocation;
                }
                else if (typeLocations.TryGetValue(typeKey, out var typeLocation))
                {
                    location = typeLocation;
                }
            }

            located.Add(new LocatedDiagnostic(diagnostic.Descriptor, diagnostic.MessageArgs, location));
        }

        return new EquatableArray<LocatedDiagnostic>(located.MoveToImmutable());
    }

    private static void AddIndexIssue(ImmutableArray<PendingDiagnostic>.Builder pending, IndexIssueModel issue)
    {
        switch (issue.Kind)
        {
            case IndexIssueKind.UnsupportedField:
                Add(pending, Diagnostics.IndexedFieldUnsupported,
                    typeKey: issue.TableFullName, memberName: issue.PropertyName,
                    args: [issue.TableFullName, issue.SchemaName, issue.PropertyName ?? "<unknown>", issue.Detail]);
                break;

            case IndexIssueKind.DuplicateField:
                Add(pending, Diagnostics.DuplicateIndexedField,
                    typeKey: issue.TableFullName, memberName: issue.PropertyName,
                    args: [issue.TableFullName, issue.SchemaName, issue.PropertyName ?? "<unknown>", issue.Detail]);
                break;

            case IndexIssueKind.SplitCompositeDeclaration:
                Add(pending, Diagnostics.CompositeIndexSplitAcrossPartials,
                    typeKey: issue.TableFullName,
                    args: [issue.TableFullName, issue.SchemaName, issue.Detail]);
                break;

            case IndexIssueKind.NullableUniqueField:
                Add(pending, Diagnostics.UniqueIndexRequiresNonNullable,
                    typeKey: issue.TableFullName, memberName: issue.PropertyName,
                    args: [issue.TableFullName, issue.SchemaName, issue.PropertyName ?? "<unknown>", issue.Detail]);
                break;

            case IndexIssueKind.NameCollision:
                Add(pending, Diagnostics.IndexNameCollision,
                    typeKey: issue.TableFullName,
                    args: [issue.TableFullName, issue.SchemaName, issue.Detail]);
                break;
        }
    }

    private static void Add(
        ImmutableArray<PendingDiagnostic>.Builder pending,
        DiagnosticDescriptor descriptor,
        string[] args,
        string? typeKey = null,
        string? memberName = null,
        LocationInfo? exactLocation = null)
        => pending.Add(new PendingDiagnostic(
            Descriptor: descriptor,
            MessageArgs: new EquatableArray<string>([.. args]),
            TypeKey: typeKey,
            MemberName: memberName,
            ExactLocation: exactLocation));

    /// <summary>Invariant-culture integer formatting — message args must be machine-independent.</summary>
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>First table full name in a CG014 cycle path ("A → B → A").</summary>
    private static string? FirstCycleNode(string cycle)
    {
        var arrow = cycle.IndexOf(" → ", StringComparison.Ordinal);
        var first = arrow < 0 ? cycle : cycle.Substring(0, arrow);
        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }

    private static TypeRef UnwrapTask(TypeRef t) => t.FullyQualifiedName.StartsWith("global::System.Threading.Tasks.Task<", StringComparison.Ordinal) && t.TypeArguments.Count > 0
        ? t.TypeArguments[0]
        : t;

    /// <summary>
    /// The recognised element-collection shapes for a <c>[Property]</c> column. Mirrors
    /// the detection in <c>TableExtractor.ResolveInlineMembers</c> / the emitters.
    /// </summary>
    private static bool IsElementCollection(TypeRef t) =>
        t.MetadataName is "System.Collections.Generic.IReadOnlyList`1"
                       or "System.Collections.Generic.IList`1"
                       or "System.Collections.Generic.List`1";

    private static string DescribeSharedShapeLiftMember(
        RelationVariantPropertyRole role,
        string name,
        string typeFullName,
        bool nullable)
    {
        var type = nullable && !typeFullName.EndsWith("?", StringComparison.Ordinal)
            ? $"{typeFullName}?"
            : typeFullName;
        return $"{role} {name}: {type}";
    }

    /// <summary>
    /// BFS from the aggregate root through <c>[Parent]</c>-pointing tables, collecting
    /// every member of <paramref name="agg"/> that the loader can reach. The aggregate
    /// loader's dotted WHERE clauses depend on this exact set; anything in
    /// <c>agg.MemberFullNames</c> not in the result triggers CG020.
    /// </summary>
    private static HashSet<string> ReachableViaParentLinks(AggregateModel agg, Dictionary<string, TableModel> byFullName)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { agg.RootFullName };
        bool added;
        do
        {
            added = false;
            foreach (var memberFullName in agg.MemberFullNames)
            {
                if (reached.Contains(memberFullName))
                {
                    continue;
                }

                if (!byFullName.TryGetValue(memberFullName, out var member))
                {
                    continue;
                }

                foreach (var p in member.Properties)
                {
                    if (!p.Kinds.HasFlag(PropertyKind.Parent))
                    {
                        continue;
                    }

                    var parentFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                    if (reached.Contains(parentFqn))
                    {
                        reached.Add(memberFullName);
                        added = true;
                        break;
                    }
                }
            }
        } while (added);
        return reached;
    }

    private static string StripGlobalAndNullable(string fqn)
    {
        const string prefix = "global::";
        if (fqn.StartsWith(prefix, StringComparison.Ordinal))
        {
            fqn = fqn.Substring(prefix.Length);
        }

        if (fqn.EndsWith("?", StringComparison.Ordinal))
        {
            fqn = fqn.Substring(0, fqn.Length - 1);
        }

        return fqn;
    }

    /// <summary>
    /// Collapses the read-side shape of a given kind (Children / References / etc.) across
    /// property declarations that carry the same attribute. The signature yields
    /// <c>(memberName, returnType)</c> so validation errors point at the actual
    /// declaration site.
    /// </summary>
    private static IEnumerable<(string Name, TypeRef Type)> EnumerateReadSideTypes(TableModel table, PropertyKind kind) => table.Properties.Where(prop => prop.Kinds.HasFlag(kind))
        .Select(prop => (prop.Name, prop.Type));

    /// <summary>
    /// The attribute names behind each set flag in <paramref name="kinds"/>, for
    /// diagnostic messages. Order matches declaration order of the enum.
    /// </summary>
    private static List<string> AutoValueLabels(AutoValueKind kinds)
    {
        var names = new List<string>(3);
        if (kinds.HasFlag(AutoValueKind.CreatedAt))
        {
            names.Add("CreatedAt");
        }

        if (kinds.HasFlag(AutoValueKind.UpdatedAt))
        {
            names.Add("UpdatedAt");
        }

        if (kinds.HasFlag(AutoValueKind.Version))
        {
            names.Add("Version");
        }

        return names;
    }

    /// <summary>
    /// True iff the property's declared type matches what the emitted SaveAsync writes
    /// for its marker: non-nullable DateTime / DateTimeOffset for the audit pair
    /// ([CreatedAt] / [UpdatedAt]); non-nullable int / long for [Version]. Nullability
    /// is rejected for all three — the library always assigns a value, and the guarded
    /// UPDATE's <c>version + 1</c> arithmetic and <c>WHERE version = $expected</c>
    /// binding need a definite operand.
    /// </summary>
    private static bool IsValidAutoValueType(PropertyModel p)
    {
        if (p.Type.IsNullable)
        {
            return false;
        }

        var fqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
        return p.AutoValue == AutoValueKind.Version
            ? fqn is "int" or "System.Int32" or "long" or "System.Int64"
            : fqn is "System.DateTime" or "System.DateTimeOffset";
    }

    /// <summary>
    /// Best-fit attribute label for diagnostic messages — picks the role attribute the
    /// user's property carries, falling back to "Relation" for forward/inverse-relation
    /// members or null when the property has no model annotation.
    /// </summary>
    private static string? AnnotationLabel(PropertyModel p) => p.Kinds switch
    {
        var k when k.HasFlag(PropertyKind.Id)        => "Id",
        var k when k.HasFlag(PropertyKind.Property)  => "Property",
        var k when k.HasFlag(PropertyKind.Parent)    => "Parent",
        var k when k.HasFlag(PropertyKind.Children)  => "Children",
        var k when k.HasFlag(PropertyKind.Reference) => "Reference",
        _ => p.RelationRole != RelationRole.None ? "Relation" : null,
    };
}
