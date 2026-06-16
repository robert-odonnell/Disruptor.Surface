---
name: AggregateRoot, membership, cross-aggregate ID-only contract
description: [AggregateRoot] marks the unit of load coordination. Membership is via [Children] reachability from the root; CG011 fires on conflict. Cross-aggregate edges expose IDs only on the read side; the typed async Session.RelateAsync<TKind> primitive is the canonical mutation.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
`[AggregateRoot]` on a `[Table]` class names it the root of an aggregate. Membership is computed by `RelationLinker.ComputeAggregates` walking `[Children]` reachability from the root — entities reached this way are owned by the aggregate. **`[Reference]` targets are NOT walked for ownership** — they're loaded with the aggregate as a transitive dependency (via the loader's `field.*` inline expansion when `[Inline]` is set) but live outside any specific aggregate. This is what keeps shared records like `Details` from triggering CG011 against both Design and Review. Conflict (an entity reachable via `[Children]` from 2+ roots) surfaces as **CG011** error.

The Sample schema today has two roots:
- **Design** owns Constraint, Epic, Feature, UserStory, AcceptanceCriteria, Test.
- **Review** owns Finding, Observation, Issue, DesignChange.
- `Details` is referenced from every entity but owned by none.

`ModelGraph.IsCrossAggregate(forwardKindFullName)` returns true when source-aggregate and target-aggregate differ. Today: `references`, `concerns`, `revises`, `assesses` (all Review→Design); within-aggregate: `restricts`, `validates`, `informs`, `cites`, `resolves`.

**Aggregate ≠ concurrency boundary.** The aggregate is a load-shape hint that lets the generator emit one nested SurrealQL SELECT per `Load{Root}Async`. It does NOT carry write coordination — concurrent writers collide at COMMIT as native `SurrealConflictException` from the SDK; there is no application-level lease, no per-aggregate writer slot. See `project_concurrency_model.md`.

**Cross-aggregate relation contract**:
- Read collection: `IReadOnlyCollection<IRecordId>` (user-declared). Generator routes through `Session.QueryRelatedIds<TKind>` / `QueryInverseRelatedIds<TKind>` — returns id endpoints from the in-session edge index without joining the entities dict (target lives in another aggregate snapshot).
- Mutation: `Session.RelateAsync<TKind>(IRecordId src, IRecordId tgt, SurrealTransaction tx, ct)` and `Session.UnrelateAsync<TKind>(IRecordId? src, IRecordId? tgt, SurrealTransaction tx, ct)`. The `TKind` marker is the emitted forward-kind class (e.g. `References`); endpoints are id-typed since at least one side lives in another snapshot. Same generic primitives serve within-aggregate (`IEntity` overload) and cross-aggregate (`IRecordId` overload). Optional overloads accept an explicit edge id (`RecordId`) and/or a payload `IReadOnlyDictionary<string, object?>` for `RELATE … CONTENT { … }`.
- Each `{Name}Id` struct implements every id-side union it's a member of (`IReferencedById`, `IConcernedById`, …), alongside `IRecordId`. The unions are the type witness for `where T : I…ById` constraints in user-side passthroughs that want endpoint type safety.

**Why:** Pretending cross-aggregate targets are accessible entities was a silent-failure trap — `QueryRelated<IEntity>` returned empty when targets weren't in the snapshot, masking the real "you need to load the other aggregate" answer. Forcing the API to expose IDs makes the snapshot boundary honest. The typed `TKind` keeps compile-time safety on the write call site without baking name-string literals into user code.

**How to apply:** When adding a new `[Table]`, decide if it's an aggregate root (mark with `[AggregateRoot]`) or a member of an existing one (link via `[Parent]`/`[Children]` to the chain). When adding a relation that crosses aggregate boundaries, expect the user-side declaration to use `IReadOnlyCollection<IRecordId>` and mutations to go through async `Session.RelateAsync<TKind>` with a transaction. If aggregate membership becomes ambiguous, expect CG011 — resolve by removing one of the `[Children]` paths or restructuring the parent links.
