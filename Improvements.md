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

1. **SurrealQL identifier quoting** — properties named `None`, `Value`, `Select`, etc.
   render bare into queries and DDL (`none` parses as the NONE literal). A candidate fix
   would be backtick-quoting at the `SurrealFormatter` chokepoint + `SchemaEmitter`, but
   it would change every emitted statement shape — validated against a live substrate
   first (2026-07-03 against SurrealDB 3.1.4; see
   [`docs/live-validation-2026-07-03.md` §3](docs/live-validation-2026-07-03.md)).
   **Status: reject-only diagnostic shipped (CG058/CG059); backtick-quoting
   deferred/optional.** The live probe's original "hybrid" read ("`order`/`group`/
   `value` are all soft-reserved, quote them") over-read the evidence — SurrealDB's own
   parser source (`crates/core/src/syn/lexer/keywords.rs` @ tag `v3.1.4`, the 44-word
   `RESERVED_KEYWORD` set — the exact set its `EscapeIdent` serializer backtick-quotes)
   shows `order`/`group`/`type`/`count`/`limit`/`start` are **not** reserved; only
   `value` (of that trio) is. The tiers split on **loud-vs-silent failure**, not on
   quoting-rescue. **CG058 (error)** rejects the 4 value literals
   (`none`/`null`/`true`/`false`) at generate time — a bare occurrence corrupts the query
   *silently* (read as the literal) and no quoting can rescue it. **CG059 (warning)**
   flags the other 40 `RESERVED_KEYWORD` words (incl. `value`/`select`) — these fail
   *loudly* (`Expected an idiom` / poisoned reads: `Failed to get field definitions`),
   caught at dev/apply time rather than silently, which is why a warning (not an error)
   is the right tier. Quoting-rescue is **not uniform** across this tier: `value`
   round-trips when backtick-quoted (B1) but statement-keywords do not — backtick-quoted
   `` `select` `` still throws in DML (B2.18–B2.20). Backtick-quoting remains deferred and
   optional: if it's picked up later it must escape iff-in-`RESERVED_KEYWORD` (mirroring
   SurrealDB's `EscapeIdent`) **and verify rescue per word** (statement-keywords may stay
   rejected), never ship without CG058 already in place (quoting does not fix a value
   literal — a backtick-quoted `` DEFINE FIELD `none` `` still silently poisons reads), and
   be justified first by a still-missing live test of a bare (unquoted) soft-reserved word
   actually failing.
2. **Variant duplicate-edge id drift** — RESOLVED (2026-07-02) by deterministic edge
   ids: the variant id anchor derives the edge row id from `(source, edge, target)`
   via `RecordId.ForEdge` before dispatch, so the duplicate path updates the same row
   id the session holds and `MarkSaved` stays truthful. A live-substrate smoke of the
   duplicate path is still worthwhile before a release tag. **Done 2026-07-03** —
   smoke[4] PASS: both saves derive the same `assesses:…` id and the null re-save writes
   NONE on the duplicate path ([validation §1](docs/live-validation-2026-07-03.md)).
3. **NONE-semantics smoke test** — the `Eq(null)`→`IS NONE` compile and the
   version-guarded `UPDATE … WHERE version = $v` shape were verified against SDK
   precedent and pinned tests, but deserve one pass against a live v3 before a
   release tag. **Done 2026-07-03** — smoke[1]/[5]/[2] all PASS against SurrealDB
   3.1.4: `IsNone`/`Eq(null)` match the omitted-field row, the NONE-guarded
   `string::contains` skips unset rows, and the stale save throws
   `SurrealVersionConflictException` ([validation §1–2](docs/live-validation-2026-07-03.md)).

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
