---
name: union-endpoints-for-relation-variants-preview-54
description: "Variants can declare [In] / [Out] as a union of record types via an interface attributed with In<TKind>/Out<TKind>-derived attribute. Members opt IN via partial declarations of their per-table I{Name}RecordId marker. All 5 phases shipped end-to-end 2026-05-12 (preview.54 tagged): foundation (efa31da), discovery (3782c3f), variant emitter + schema emitter + diagnostics + sample + tests."
metadata:
  node_type: memory
  type: project
  originSessionId: 68d22fb0-827a-4939-ae49-c2099b2ed83a
---

Shipped preview.54 in three commits on 2026-05-12. Extends [[project_relation_as_class_redesign]] (preview.51's variant-as-class shape) to handle endpoints that span multiple `[Table]` types.

**Implementation status — DONE.**
- Phase 1 — `efa31da`. `In<TKind>` / `Out<TKind>` attribute bases. Per-table `I{Name}RecordId : IRecordId` interface emitted by `IdEmitter` alongside each `{Name}Id` struct; struct's base list includes the marker.
- Phase 2 — `3782c3f`. Two-pass discovery in `UnionEndpointExtractor`: pass (a) finds interfaces attributed with anything deriving from `In<TKind>` / `Out<TKind>` (walks attribute class's base chain for the constructed-generic forms `In`1` / `Out`1`); pass (b) finds partial `I{Name}RecordId` decls with non-empty base lists. `RelationLinker.ComputeUnionEndpoints` stitches candidates into `UnionEndpointModel`s; marker→table resolution strips `I`-prefix and `RecordId`-suffix, prefers same-namespace match. `ModelGraph.UnionEndpoints` + `ModelGraph.FindUnionEndpoint(FQN)` expose results.
- Phase 3 — `RelationVariantEmitter` branches on `graph.FindUnionEndpoint(...)` for `[In]`/`[Out]` types. Union endpoints get a single backing field of the union interface; `Hydrate` switches on `__rid.Table` to construct the matching `{Name}Id` and cast to the union interface (default arm throws fail-fast); `EnumerateReferences` routes through `RecordId.From(IRecordId)`; `SaveAsync` skips the forward-dep walk for union endpoints (id-only like cross-aggregate typed-ids); per-kind hydration dispatcher Cartesian-expands over union members.
- Phase 4 — `SchemaEmitter.CollectEndpointTables` consults `graph.FindUnionEndpoint(...)` and contributes every `UnionEndpointModel.MemberTableFullNames` entry to the edge table's FROM/TO. Result: `TYPE RELATION FROM source TO a|b|c`.
- Phase 5 — CG031 (kind mismatch — union's `KindFullName` != variant's forward kind) reported per-endpoint in `RelationVariantEmitter`. CG032 (dead union — interface attributed but no per-table marker partial enrols any table; warning, not error) reported in `ModelGenerator.Emit`. Sample model: new `[Pertains]` kind, `[PertainsTarget]`-attributed `IPertainsTarget` union, Constraint + UserStory enrolled, single `IssuePertainsTarget` variant. 8 new emission shape tests (269/269 green total).

**User-side surface (worked example from Sample).**

```csharp
// Disruptor.Surface.Sample.Relations
public sealed class PertainsAttribute : ForwardRelation;
public sealed class PertainedByAttribute : InverseRelation<PertainsAttribute>;

// Parameterless union-marker attribute deriving from Out<TKind>:
public sealed class PertainsTargetAttribute : Out<PertainsAttribute>;

// The union interface itself — apply the marker to it:
[PertainsTarget] public partial interface IPertainsTarget : IRecordId;

// Disruptor.Surface.Sample.Models — per-table opt-ins (same namespace as the emitted I{Name}RecordId)
public partial interface IConstraintRecordId : IPertainsTarget;
public partial interface IUserStoryRecordId : IPertainsTarget;

// Disruptor.Surface.Sample.Relations.Variants — single variant covers every member:
[Pertains]
public partial class IssuePertainsTarget {
    [In]  public partial IssueId Source { get; set; }
    [Out] public partial IPertainsTarget Target { get; set; }
}

// Use site:
await session.SaveAsync(new IssuePertainsTarget { Source = issueId, Target = (IPertainsTarget)constraintId }, tx);
await session.SaveAsync(new IssuePertainsTarget { Source = issueId, Target = (IPertainsTarget)userStoryId }, tx);

// Pattern-match on read:
if (variant.Target is IConstraintRecordId cId) { /* … */ }
```

**Why this spelling (and why we rejected the alternatives):**
- `[Out(typeof(IFooTarget))]` (parameters in attributes) violates the "no parameters at use-site" principle — `[Restricts]`, `[In]`, `[Out]` are all parameterless today; `RestrictedByAttribute : InverseRelation<RestrictsAttribute>` is the canonical parameter-via-inheritance pattern. Stays consistent.
- `partial interface IFooTarget : IARecordId, IBRecordId` (membership at union site) reads naturally as a union but compiles as conjunction — C# requires implementers to satisfy ALL bases, so no concrete `{Name}Id` can satisfy `IFooTarget` and it's uninstantiable. Flipping to member-opts-in is the C#-correct shape.
- "Endpoint is plain `RecordId`" (no union interface) is the fallback if we want zero interface emission cost, but loses the type-system benefit of the property documenting membership.

**Degenerate "union of size 1":** today's single-table case (`[Out] partial Epic Target`) is unchanged — it's the size-1 union, no interface needed. No migration burden on existing variants. See [[project_typed_ids]] for the `{Name}Id` struct shape and [[project_relation_unions]] for the existing per-kind id-side markers (`I{InverseName}Id`) which the new per-table `I{Name}RecordId` is symmetric with.

**Note on the per-kind vs per-table markers:** preview.51 emits per-kind id-side union markers `I{InverseName}Id` (e.g., `IRestrictedById`) when a kind has 2+ members in its target set. Per-table `I{Name}RecordId` is the same primitive at a finer grain — per [Table] rather than per kind. They coexist; the kind-level marker is computed at link time from variant collation, the per-table marker is emitted unconditionally as a primitive for unions to extend.
