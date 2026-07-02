# Improvements backlog

Refreshed 2026-07-02 against the state of this branch. The original gap analysis
(pre-preview.59) is superseded: many items have since shipped — entity indexes incl.
composites (preview.59), `CountAsync` + `StartsWith`/`EndsWith`/`NotIn`/`Between`,
schema fingerprint, and — from the 2026-07-02 review-and-fix pass
(`docs/review-2026-07-02.md`) — audit columns (`[CreatedAt]`/`[UpdatedAt]`),
optimistic concurrency (`[Version]` + `SurrealVersionConflictException`), bulk save
(batched `INSERT INTO` via the `ISaveContext` dispatch seam), `ExistsAsync` /
`FirstOrDefaultAsync` / `Single(OrDefault)Async`, `IsNone`/`IsNotNone` /
`IsNullOrEmpty` / `Matches` / ignore-case predicates, primitive-element collections,
session `CloseReason`, diagnostic source locations with a split diagnostics output,
and the CG042–CG056 fail-closed diagnostics family.

## Still open — needs a live SurrealDB to validate

1. **SurrealQL identifier quoting** — properties named `Order`, `Group`, `None`, etc.
   render bare into queries and DDL (`none` parses as the NONE literal). Fix is
   backtick-quoting at the `SurrealFormatter` chokepoint + `SchemaEmitter`, but it
   changes every emitted statement shape — validate against a live substrate first.
2. **Variant duplicate-edge id drift** — RESOLVED (2026-07-02) by deterministic edge
   ids: the variant id anchor derives the edge row id from `(source, edge, target)`
   via `RecordId.ForEdge` before dispatch, so the duplicate path updates the same row
   id the session holds and `MarkSaved` stays truthful. A live-substrate smoke of the
   duplicate path is still worthwhile before a release tag.
3. **NONE-semantics smoke test** — the `Eq(null)`→`IS NONE` compile and the
   version-guarded `UPDATE … WHERE version = $v` shape were verified against SDK
   precedent and pinned tests, but deserve one pass against a live v3 before a
   release tag.

## Still open — design/feature work

4. **Content-addressed edge ids** — SHIPPED (2026-07-02) as the default: the variant
   save path derives `RecordId.ForEdge("{src}|{edge}|{tgt}")` ids automatically (item
   2's resolution), so the opt-in-attribute framing is obsolete. `RecordId.Idempotent`
   + `Resolve` remain for hand-minted edge rows.
5. **Delete-by-query / update-by-query** — `Query.X.Where(...).DeleteAsync(tx)`;
   update-by-query needs a typed setter design.
6. **`[Assert]` value validators and PERMISSIONS clauses** in emitted DDL.
7. **Migration diff** — fingerprint shipped; `GenerateDiffAsync`/rename helpers didn't.
8. **`[Table]` inheritance** — shared base-class columns (audit fields would pair well).
9. **GROUP BY / aggregates; FETCH clause; `IAsyncEnumerable` streaming; cursor
   pagination.**
10. **Live queries (`LIVE SELECT`)** — the big structural one.
11. **Full-text / vector indexes** (`SEARCH ANALYZER`, `MTREE`, `HNSW`).
12. **Owned/inline POCO in scalar position** (`[Property] Address Address`).
13. **Multi-assembly composition** (one `[CompositionRoot]` per compilation today).
14. **Union marker simple-name resolution** — cross-namespace fallback picks the
    first-collected table; should resolve semantically at extraction.
15. **Unmapped-scalar coverage** — `byte[]`, `TimeSpan`, enums, `Uri`, geometry are
    still not schema-mappable (table-level unmapped scalars are a CG025 error;
    variant payloads warn via CG056).
16. **Edge-query payload materialisation** — the edge terminal returns endpoint pairs;
    payload requires a second query.
17. **Ecosystem packages** — DI wiring, `ILogger`/`ActivitySource` seams, a public
    `Disruptor.Surface.Testing` package with an in-memory substitute.
18. **AOT/trim story** — residual `Activator.CreateInstance` in hydration.
19. **Per-element emit granularity** — the diagnostics output is split (2026-07-02),
    but emission is still one monolithic node; per-table outputs would confine
    re-emission and pair with `WithTrackingName` cache assertions.
