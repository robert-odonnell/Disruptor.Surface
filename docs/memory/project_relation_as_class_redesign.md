---
name: Relation-as-class redesign — preview.51 DONE 2026-05-12
description: historical: ForwardRelation<TPayload> replaced by [Restricts]-on-class + [In]/[Out]/[Property] variant entities. All phases shipped end-to-end; 231 tests passing. Recommend the new shape — see docs/notes.md preview.51 for canonical reference.
metadata:
  type: project
  originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---

The relation-as-class redesign is complete as of 2026-05-12. Variants are entities (`IRelationVariant : IEntity`); edge mutations go through `Session.SaveAsync(new TVariant { Source = src, Target = tgt }, tx)` instead of `Session.RelateAsync<TKind>(...)`. Multi-variant kinds get `SCHEMALESS`; single-variant kinds keep `SCHEMAFULL`. Per-kind `{KindName}Id`, `I{KindName}Variant`, and `{KindName}Hydration.HydrateVariant` are emitted.

**Why:** Multi-variant relations need a shape that carries different payloads per (source, target) pair; the old `ForwardRelation<TPayload>` was single-payload-per-kind, which was the wrong shape for the code-indexer use case. The variant **is** an entity, which means the existing Save / Delete / Track machinery works without a parallel relation API. The 8 `Session.RelateAsync` overloads, `RelateAsyncReplace`, `{Marker}RelateExtensions`, and `EdgePredicateFactoryEmitter` are all deleted.

**How to apply:** Recommend the new shape end-to-end. Declare e.g. `[Restricts] public partial class ConstraintRestrictsUserStory { [In] public partial Constraint Source { get; set; } [Out] public partial UserStory Target { get; set; } }` plus any `[Property]` payload members. To create an edge: `await session.SaveAsync(new ConstraintRestrictsUserStory { Source = constraint, Target = story }, tx)`. For cross-aggregate edges, type endpoints as the typed id (e.g. `[In] public partial IssueId Source`). Sync entity-side `[Restricts] partial IReadOnlyCollection<...>` read collections are unchanged.

Variant query endpoint-resolution gotcha: `QueryVariantsOutgoingAsync<TVariant>` returns hydrated variants whose entity-typed endpoints resolve through the session's identity map. If endpoints aren't loaded, the property getter throws `Endpoint 'X' is not set.`. Three escape hatches: (a) reuse the load-session so within-aggregate endpoints resolve cleanly; (b) read raw `(field, RecordId?)` tuples via `((IEntity)v).EnumerateReferences()`; (c) declare typed-id endpoints so `Source` / `Target` return the typed id directly. See [[project_loader]] for how aggregate load + session identity map combine.
