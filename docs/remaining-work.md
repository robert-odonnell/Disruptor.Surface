# Remaining work after PR #6

Written 2026-07-02, at the close of the review-and-fix PR (475/475 tests, review in
[`review-2026-07-02.md`](review-2026-07-02.md), maintained backlog in
[`../Improvements.md`](../Improvements.md)). This document is the working plan for
what comes next: what needs a live SurrealDB, what's pure dev work, and the open
questions that need an owner decision before someone picks the item up.

---

## 1. Live-substrate validation pass (do this first)

> **DONE 2026-07-03** — ran against a live ephemeral SurrealDB **3.1.4** (memory
> backend, fresh DB). **All 7 shapes PASS** (harness exit 0); full results, the item-2b
> exception-ordering observation, and the quoting verdict are in
> [`live-validation-2026-07-03.md`](live-validation-2026-07-03.md). No compile/emit
> fallout — the pinned tests already encoded the shapes correctly.

Everything in the PR was verified against the SDK's own usage, pinned-SQL tests, and
the recording-fake harness — but none of it has touched a real SurrealDB v3 from the
fix environment. Before the next release tag, run the sample harness
(`dotnet run --project src/Disruptor.Surface.Sample` against a local
`surreal start`) and additionally smoke these specific shapes:

| # | What to verify | Why it's on the list |
| --- | --- | --- |
| 1 | `Eq(null)` / `IsNone()` compile to `(field IS NONE OR field IS NULL)` and actually match rows whose field was omitted at save time | The NONE-vs-NULL semantics were derived from documented SurrealQL behavior, not observed |
| 2 | `[Version]` guarded update: `UPDATE $_record_id CONTENT $_content WHERE {field} = $_expected_version` returns an empty statement result on version mismatch (that's what triggers `SurrealVersionConflictException`) | The empty-result-on-no-match detection is the linchpin of conflict detection |
| 3 | Bulk save: `INSERT INTO $_table $_records` with per-record embedded `id` creates all rows; a mixed batch inside one transaction sees earlier statements (a child UPSERT referencing a same-batch parent) | The flush-ordering correctness argument assumes sequential statement visibility in-txn |
| 4 | Deterministic edge ids: saving the same `(source, target)` variant twice hits `ON DUPLICATE KEY UPDATE` on the row whose id equals `RecordId.ForEdge(...)`; payload updates land; `SurrealValue.None` binding (CBOR tag 6) writes NONE on the duplicate path | New wire behavior introduced in the final round |
| 5 | NONE-guarded string functions (`field != NONE AND string::contains(...)`) skip unset rows instead of erroring the SELECT | The strict-function-typing failure mode was the reason for the guard |
| 6 | `string::matches` regex predicate syntax | Chosen over `~`/`?~` (those are fuzzy-match); never run live |
| 7 | Audit columns round-trip: CREATE stamps both, UPDATE refreshes only `updated` | Simple, but new emit |

If any of these misbehave, the pinned tests encode the intended shape — fix the
compile/emit, not the test's intent.

## 2. Deferred fixes that NEED the live substrate before implementation

- **SurrealQL identifier quoting** (review §5). Properties named `Order`, `Group`,
  `None`, `Value`, etc. render bare into queries and DDL (`none` parses as the NONE
  literal → silently always-false predicates). The fix — backtick-quote every
  identifier at the `SurrealFormatter` chokepoint and in `SchemaEmitter` DDL —
  changes every emitted statement, so it needs live confirmation that backtick
  quoting is accepted in every position we emit (DEFINE FIELD/INDEX columns, WHERE,
  ORDER BY, SET, subselect aliases). Alternative if quoting misbehaves anywhere: a
  generator diagnostic rejecting reserved-word names (smaller, but a word-list to
  maintain). **Owner call needed on which approach** — see Questions below.
  **Live verdict 2026-07-03 (SurrealDB 3.1.4): do BOTH — it's a hybrid.** Backticks
  *are* accepted in every emit position for soft reserved words (`order`/`group`/`value`
  round-trip through DEFINE FIELD/INDEX, CREATE/UPDATE SET, WHERE, ORDER BY, subselect
  alias), so the chokepoint-quoting fix is viable and should ship. But value literals
  `none`/`null`/`true`/`false` and the keyword `select` are **unrescuable by quoting**:
  `SET `none` = …` errors `Expected an idiom`, and a bare `DEFINE FIELD `none`` is
  accepted yet silently poisons the whole table's reads (`Failed to get field
  definitions`). So the reserved-word diagnostic is still needed — scoped to exactly
  that unrescuable set. Full evidence + per-position table:
  [`live-validation-2026-07-03.md` §3](live-validation-2026-07-03.md).
- **Duplicate-path smoke for pre-existing data** — see Question 3.

## 3. Dev-ready backlog (no substrate needed to start)

In rough value order; details in `Improvements.md`:

1. **Delete-by-query** (`Query.X.Where(...).DeleteAsync(tx)`) — the "prune superseded
   run" pattern. Update-by-query needs a typed setter design first (question 6).
2. **`[Assert]` validators + PERMISSIONS clauses** in emitted DDL.
3. **`[Table]` inheritance** — shared base-class columns; pairs well with the new
   audit attributes.
4. **Migration diff** — fingerprint exists; `GenerateDiffAsync`/rename helpers don't.
5. **GROUP BY / aggregates, FETCH, `IAsyncEnumerable`, cursor pagination.**
6. **Union marker semantic resolution** — replace the simple-name/first-collected
   fallback with symbol resolution at extraction time.
7. **Per-element emit granularity** — the diagnostics output is already split; the
   emit output is still one monolithic node.
8. **Ecosystem packages** — DI wiring, `ILogger`/`ActivitySource`,
   `Disruptor.Surface.Testing` with an in-memory substitute.
9. **Live queries (`LIVE SELECT`)** — the big structural one; needs the substrate
   for development, not just validation.
10. **AOT/trim** — residual `Activator.CreateInstance` in hydration.

## 4. Outstanding questions (owner decisions)

1. **Identifier quoting strategy** — quote everything (robust, big diff churn, needs
   live validation) vs. reserved-word diagnostic (fail-closed, needs a word list)?
   **Now informed (live 2026-07-03):** the answer is neither-alone but *both* — quoting
   works for the soft-reserved majority, but a small unrescuable value-literal set
   (`none`/`null`/`true`/`false`/`select`) still needs a diagnostic. See §2 and
   [`live-validation-2026-07-03.md` §3](live-validation-2026-07-03.md).
2. **Non-nullable variant payload defaults** — a non-nullable `string` payload left
   at its (null) backing default still omits its binding on the duplicate-update
   path (pre-existing, flagged in the PR discussion). Options: emit a schema
   `DEFAULT ""`, treat it as a save-time error, or leave it. Nullable payloads are
   already handled (bind NONE).
3. **Deterministic edge ids vs. pre-existing data** — edges created before this PR
   carry random Ulid ids; a replayed save now derives a hash id, so the UNIQUE(in,
   out) duplicate path updates the *old* row while the session holds the hash id —
   the old drift, but only for pre-PR rows. Given preview status, is "wipe/reseed
   dev databases" an acceptable answer, or do we need a one-time migration note?
4. **CG056 severity** — unmapped relation-variant payload types currently *warn* and
   emit fail-soft (in-memory-only field). Before this PR the same shape was a hard
   compile error, so nothing can be relying on the lenient behavior; argument exists
   for making it an error. Keep warning or promote?
5. **`IsNone()` contract** — currently matches unset **or** null (same compiled shape
   as `Eq(null)`), on the grounds that non-library writers may store explicit NULLs.
   Confirm, or split into `IsUnset()` (strict NONE) + `IsNullValue()`?
6. **Update-by-query setter design** — `Query.X.Where(...).UpdateAsync(?, tx)`
   needs a typed SET expression surface (probably mirroring `PropertyExpr`). Worth
   sketching before implementation; delete-by-query doesn't need to wait for it.
7. **Version/audit conventions** — `[Version]` starts at 1 on CREATE; audit/version
   markers require an explicit `[Property]` alongside them (CG052). Both were
   judgment calls this PR — flag now if you want different conventions before they
   calcify.
8. **`ProjectionQuery` terminals** — `ExistsAsync`/`CountAsync` were deliberately not
   mirrored onto projections (they're shape-independent; call them on the underlying
   query before `.Select(...)`). Confirm or ask for the mirror.

## 5. Merge notes

- The PR lowers the generator's Roslyn pin to 5.0.0 — it builds on both SDK 10.0.1xx
  and 10.0.3xx lines.
- `Improvements.md` on this branch is the refreshed, maintained backlog; the original
  pre-review version is superseded.
- Diagnostics now span CG001–CG056 with source locations; the incremental-caching
  regression tests (`trackIncrementalGeneratorSteps`) are the guardrail for future
  pipeline changes — keep them green.
