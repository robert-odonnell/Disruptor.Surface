---
name: shared-shape-relation-interfaces-preview-55
description: "preview.55 (2026-05-12). User declares a `partial interface : IRelationVariant` over relation variants with matching endpoint+payload shape; the generator emits `static I Create<TKind>(Action<I> init)` onto it. Kind-keyed construction only — polymorphic queries deliberately remain a user-side concern; relation kinds stay distinct edge tables."
metadata: 
  node_type: memory
  type: project
  originSessionId: 1fd960d5-e72a-4f6c-ba01-0efccf0ddb5b
---

A user-declared `partial interface ISomething : IRelationVariant` whose members match the endpoint and payload shape of two or more relation variants becomes a "shared-shape contract". Each implementing variant adds it to its base list (`[Calls] partial class CallsRelation : ICodeSymbolEdge`). The generator emits a partial fragment of the interface with one static factory:

```csharp
public static ICodeSymbolEdge Create<TKind>(Action<ICodeSymbolEdge> init)
    where TKind : IRelationKind {
    if (typeof(TKind) == typeof(global::M.Calls))      __instance = new global::M.CallsRelation();
    else if (typeof(TKind) == typeof(global::M.References)) __instance = new global::M.ReferencesRelation();
    else throw new ArgumentException(...);
    init(__instance);
    return __instance;
}
```

**Scope is narrow by design.** The generator does NOT emit a polymorphic query surface (no "all variants of these kinds" `QueryOutgoingAsync<I>`). Per-kind dispatch on the read side stays the user's job — each kind remains a distinct edge table; the shared-shape interface gives uniform `IEnumerable<I>` handling on results but the user still calls `QueryVariantsOutgoingAsync<TVariant>` per kind to gather them. This framing was explicit in the spec conversation: "you still need a dispatch point from relation kind to concrete variant type", "it does not create a generated 'all variants of these kinds' query surface", "it does not remove the need for concrete variant classes; it just makes them easier to handle uniformly."

**Why not more?** The construction-side switch was real boilerplate every caller had to maintain. The query-side fan-out is closer to substrate concerns (multi-table SELECT, per-table hydration) and the user-side glue is fine — kinds are distinct edge tables and that's a fundamental property to preserve.

**Discovery:**
- `SharedShapeExtractor` finds partial interfaces whose transitive base chain includes `IRelationVariant`. Excludes the runtime interface itself and union-endpoint interfaces (which derive from `IRecordId` — that's preview.54's territory).
- `RelationVariantExtractor` captures `ImplementedInterfaceFullNames` (filtered to non-runtime).
- `RelationLinker.ComputeSharedShapes` joins the two: every variant listing the interface in its bases becomes a `SharedShapeVariantBinding(VariantFqn, KindMarkerFqn, EdgeName)`. Common source/target endpoint types are tracked but unused by the emitter.

**Emission:**
- `SharedShapeEmitter` writes one `{InterfaceFqn}.SharedShape.g.cs` per `SharedShapeModel`. Partial interface fragment with one static method. `where TKind : IRelationKind` constraint pins the generic argument to the marker classes `RelationKindEmitter` already emits.

**Diagnostics:**
- CG033 (error): shared-shape interface must be `partial`.
- CG035 (warning): shared-shape interface has no implementing variants.

**Sample:** `src/Disruptor.Surface.Sample/Spike/` carries the working example — `ICodeSymbolEdge` over `CallsRelation` + `ReferencesRelation`, both on the new `CodeSymbol` aggregate. Originally a no-generator-help spike that proved the partial-property contract; promoted to ship as the canonical demo once the factory feature landed.

**See also:** [[project_union_endpoints_design]] for the orthogonal preview.54 feature (one kind, multiple endpoint types vs. preview.55's multiple kinds, one shape).
