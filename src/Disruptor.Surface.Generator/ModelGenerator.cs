using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Emit;
using Disruptor.Surface.Generator.Model;
using Disruptor.Surface.Generator.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class ModelGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Record declarations are admitted alongside classes so the pipeline can REJECT
        // them with CG048 — AttributeTargets.Class allows `[Table] partial record X`,
        // and a class-only predicate silently ignored it (clean compile, no output).
        var tables = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AnnotationsMetadata.Table,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, ct) => TableExtractor.TryExtract(ctx, ct))
            .Where(static t => t is not null)
            .Select(static (t, _) => t!);

        // Relation kinds are discovered by base-walk, not by marker attribute — any class
        // that derives from ForwardRelation / InverseRelation<T> qualifies. Predicate is
        // syntax-only ("has a base list") so the incremental pipeline still caches well;
        // the transform uses the semantic model to confirm and split by direction.
        var allRelationKinds = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: RelationKindExtractor.IsClassWithBaseList,
                transform: static (ctx, ct) => RelationKindExtractor.TryExtract(ctx, ct))
            .Where(static k => k is not null)
            .Select(static (k, _) => k!);

        var forwardKinds = allRelationKinds.Where(static k => k.Direction == RelationDirection.Forward);
        var inverseKinds = allRelationKinds.Where(static k => k.Direction == RelationDirection.Inverse);

        // Relation variants — classes carrying a [Restricts] / [RestrictedBy] (etc.) attribute,
        // each declaring [In] / [Out] endpoints and optional [Property] payload fields. The
        // same attribute classes appear elsewhere as property-on-entity annotations (handled
        // by TableExtractor); here we pick up only the on-class usage.
        var relationVariants = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: RelationVariantExtractor.IsClassWithAttributeList,
                transform: static (ctx, ct) => RelationVariantExtractor.TryExtract(ctx, ct))
            .Where(static v => v is not null)
            .Select(static (v, _) => v!);

        var compositionRoots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AnnotationsMetadata.CompositionRoot,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, ct) => CompositionRootExtractor.TryExtract(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!);

        // Union endpoint discovery — two passes feed the linker. Pass (a) finds interfaces
        // attributed with anything deriving from In<TKind>/Out<TKind> (the union itself).
        // Pass (b) finds partial I{Name}RecordId decls with a non-empty base list (the
        // per-table opt-ins). The linker stitches them into UnionEndpointModels.
        var unionInterfaceCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: UnionEndpointExtractor.IsInterfaceWithAttributeList,
                transform: static (ctx, ct) => UnionEndpointExtractor.TryExtractUnionInterface(ctx, ct))
            .Where(static u => u is not null)
            .Select(static (u, _) => u!);

        var unionMembershipCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: UnionEndpointExtractor.IsPerTableMarkerWithBases,
                transform: static (ctx, ct) => UnionEndpointExtractor.TryExtractMembership(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Shared-shape interfaces: user-declared interfaces deriving from IRelationVariant
        // gain a generated static Create<TKind> factory so relation rows can be constructed
        // by kind without hand-maintaining a switch. Union-endpoint interfaces (derived
        // from IRecordId) are filtered out by the extractor.
        var sharedShapeCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: SharedShapeExtractor.IsInterfaceWithBaseList,
                transform: static (ctx, ct) => SharedShapeExtractor.TryExtract(ctx, ct))
            .Where(static s => s is not null)
            .Select(static (s, _) => s!);

        // Declaration-location side channel — syntax-only map of every type declaration's
        // (and attributed property's) identifier location, keyed by NormaliseFullName.
        // Position-sensitive BY DESIGN, so it feeds ONLY the diagnostics output below;
        // combining it into the graph would make every emitter re-run on every edit.
        var declarationLocations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: DeclarationLocationExtractor.IsTypeDeclaration,
                transform: static (ctx, ct) => DeclarationLocationExtractor.Extract(ctx, ct))
            .Where(static l => l is not null)
            .Select(static (l, _) => l!);

        var graph = tables.Collect()
            .Combine(forwardKinds.Collect())
            .Combine(inverseKinds.Collect())
            .Combine(compositionRoots.Collect())
            .Combine(relationVariants.Collect())
            .Combine(unionInterfaceCandidates.Collect())
            .Combine(unionMembershipCandidates.Collect())
            .Combine(sharedShapeCandidates.Collect())
            .Select(static (combined, _) =>
                RelationLinker.Build(
                    combined.Left.Left.Left.Left.Left.Left.Left,    // tables
                    combined.Left.Left.Left.Left.Left.Left.Right,   // forwardKinds
                    combined.Left.Left.Left.Left.Left.Right,        // inverseKinds
                    combined.Left.Left.Left.Left.Right,             // compositionRoots
                    combined.Left.Left.Left.Right,                  // relationVariants
                    combined.Left.Left.Right,                       // unionInterfaceCandidates
                    combined.Left.Right,                            // unionMembershipCandidates
                    combined.Right))                                // sharedShapeCandidates
            .WithTrackingName("ModelGraph");

        // Diagnostics are computed from the graph as position-independent pending
        // records, then located against the declaration map in a separate node, and
        // reported from their OWN output. Split rationale (see docs/architecture.md,
        // "Diagnostic source locations"): the emit output's input stays the graph alone,
        // so a position-only edit never re-runs the emitters; only the cheap diagnostics
        // output re-resolves locations — and when there are no diagnostics the resolved
        // (empty) set is value-equal run-to-run, so even that output stays cached.
        var pendingDiagnostics = graph
            .Select(static (g, _) => ModelValidation.Validate(g).Diagnostics)
            .WithTrackingName("PendingDiagnostics");

        var locatedDiagnostics = pendingDiagnostics
            .Combine(declarationLocations.Collect())
            .Select(static (pair, _) => ModelValidation.Locate(pair.Left, pair.Right))
            .WithTrackingName("LocatedDiagnostics");

        context.RegisterSourceOutput(locatedDiagnostics, static (spc, diagnostics) => ReportDiagnostics(spc, diagnostics));
        context.RegisterSourceOutput(graph, static (spc, g) => Emit(spc, g));
    }

    /// <summary>
    /// The diagnostics-only output: rehydrates each resolved <see cref="LocationInfo"/>
    /// into a reportable <see cref="Location"/> (external-file location — path + spans;
    /// never a retained syntax tree) and reports. Diagnostics with no resolvable
    /// declaration fall back to <see cref="Location.None"/>.
    /// </summary>
    private static void ReportDiagnostics(SourceProductionContext spc, EquatableArray<LocatedDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var args = new object[diagnostic.MessageArgs.Count];
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = diagnostic.MessageArgs[i];
            }

            spc.ReportDiagnostic(Diagnostic.Create(
                diagnostic.Descriptor,
                diagnostic.Location?.ToLocation() ?? Location.None,
                args));
        }
    }

    /// <summary>
    /// The emit output. All CG validation lives in <see cref="ModelValidation.Validate"/>
    /// and is REPORTED from the separate diagnostics output; this method re-runs the same
    /// (cheap, pure) validation only to keep the fail-closed emitter-skip decisions —
    /// invalid composition roots / aggregates / tables must not drag half-broken source
    /// into the consumer compilation.
    /// </summary>
    private static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        var validation = ModelValidation.Validate(graph);

        foreach (var union in graph.Unions)
        {
            UnionInterfaceEmitter.Emit(spc, union);
        }

        // CG018/CG019 (reported from the diagnostics output) skip the emitter — avoids
        // dragging half-broken Load{Root}Async methods into the consumer compilation.
        if (validation.CompositionRootValid)
        {
            CompositionRootEmitter.Emit(spc, graph);
        }

        RelationKindEmitter.Emit(spc, graph);

        // CG020 skips the aggregate loaders — a member without a [Parent] path back to
        // the root would be silently unloadable.
        if (validation.LoaderValid)
        {
            AggregateLoaderEmitter.Emit(spc, graph);
        }

        ReferenceRegistryEmitter.Emit(spc, graph);
        SchemaEmitter.Emit(spc, graph);
        QueryRootEmitter.Emit(spc, graph);
        EdgeQueryRootEmitter.Emit(spc, graph);
        HydrateRootEmitter.Emit(spc, graph);
        LoadEntryEmitter.Emit(spc, graph);

        foreach (var table in graph.Tables)
        {
            // CG001 (not partial) / CG008 (multiple [Id]) — skip every per-table emitter.
            if (validation.SkippedTables.Contains(table.FullName))
            {
                continue;
            }

            IdEmitter.Emit(spc, table, graph);
            PredicateFactoryEmitter.Emit(spc, table);
            IdsAsyncEmitter.Emit(spc, table);
            TraversalBuilderEmitter.Emit(spc, table, graph);

            // Any other per-table validation failure (CG022/CG024/…/CG055) — keep the
            // id/query surface but skip the entity partial, whose emission depends on a
            // well-formed property model.
            if (validation.PartialSuppressedTables.Contains(table.FullName))
            {
                continue;
            }

            PartialEmitter.Emit(spc, table, graph);
        }

        // Per-variant relation classes — emits IEntity scaffolding, [In]/[Out]/[Property]
        // backing fields, Hydrate / SaveAsync. Per-kind sidecars (variant marker interface,
        // hydration dispatcher) emit alongside. (CG029/CG030/CG031 are still reported from
        // inside the emitter itself.)
        RelationVariantEmitter.Emit(spc, graph);

        // Per-shared-shape interface: emit a partial fragment carrying a typed
        // Create<TKind>(Action<I> init) factory keyed off the per-kind marker class.
        SharedShapeEmitter.Emit(spc, graph);
    }
}
