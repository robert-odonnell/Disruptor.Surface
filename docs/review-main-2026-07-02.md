# Code review — 2026-07-02 (main / `master`)

Scope: current default branch (`master`) after the 2026-07-02 review-and-fix pass. This is a source review of the generator pipeline, emitted contracts, runtime session boundary, relation-variant persistence, query layer, docs, and backlog. I did not run a live SurrealDB substrate in this pass; anything that needs live parser/runtime validation is called out separately.

Severity: **H** = can corrupt persisted/session identity or break users in a normal flow; **M** = real defect with plausible trigger; **L** = docs / DX / latent robustness.

## Verdict

Main is materially stronger than the reviewed branch. The big session-boundary issues that worried me are fixed: `FetchAsync` is now pinned to an already-tracked root, foreign root rows are rejected fail-closed, `ExecuteIntoSessionAsync` closes a supplied session on hydration/wire failure, nullable relation payload duplicate-update variables bind explicitly, and relation variants now derive deterministic edge ids for the normal endpoint-first save path.

I would still hold a release tag until the relation-variant id escape hatch below is fixed. It is not a theoretical style gripe; it can recreate the exact identity-map lie the deterministic-edge-id work was meant to remove.

## Verified fixed since the previous review

### Fetch/session boundary is now correct

`FetchAsync` now requires `SurfaceQuery<T>.WithId(...)`, rejects pin-less queries before dispatch, requires the pinned id to already be tracked, and rejects any returned root row whose id differs from the pin. Nested includes may still hydrate new child/target rows, but the root can no longer be invented into the session. The regression tests cover pin-less query, unknown pinned id, foreign response row, and legitimate rehydrate-over-existing behavior.

This addresses the earlier trust-boundary concern.

### Public session hydration now fails closed

`SurfaceQuery<T>.ExecuteIntoSessionAsync(session, ...)` now wraps dispatch/hydration in a try/catch and calls `session.CloseAsFailed(SessionCloseKind.HydrationFailed, ex)` before rethrow. That closes the hole where a caller-supplied session could be left half-mutated but still usable.

### Nullable relation payload duplicate updates now bind variables

Relation-variant save generation now branches nullable payload bindings: null binds `SurrealValue.None`, non-null goes through `ContentValue.Set`. This fixes the previous `ON DUPLICATE KEY UPDATE field = $_p_field` with an unbound `$_p_field` variable.

### Deterministic edge ids were added for the normal path

`RecordId.ForEdge(edgeTable, source, target)` derives the row id from `(source, edge, target)`, and the relation-variant id anchor now uses it when both endpoints are available. The tests pin same-endpoint saves producing the same id, different targets producing different ids, and nullable payload behavior.

That normal path is good. The escape hatch below is the remaining problem.

---

## Findings

### 1. User-assigned or prematurely-read relation variant ids can still recreate duplicate-edge id drift — H

The deterministic edge-id fix only works if every save of the same `(in, edge, out)` triple chooses the same row id. Main still allows two ways around that.

First, a relation variant can expose a user-facing `[Id]` property, and the generated id anchor deliberately lets a user-assigned id win over the deterministic `RecordId.ForEdge(...)` derivation. The tests explicitly verify `E2E_UserAssignedId_WinsOverDerivation` and assert the assigned id differs from `RecordId.ForEdge(...)`.

Second, if the variant id is read before both endpoints are resolvable, `__MintId()` falls back to `{Kind}Id.New()` and stores that random id in `_id`. If endpoints are set later, the random id remains. The comment says the save path fails before dispatch for unset endpoints, but that only covers the case where endpoints are still unset at save time. It does not cover “read id early, then set endpoints, then save.”

Why this matters:

1. Relation tables have a unique index on `(in, out)`.
2. Variant save emits `INSERT RELATION ... $_content ON DUPLICATE KEY UPDATE ...` for payload variants, or `INSERT RELATION IGNORE ...` for payload-less variants.
3. After the query succeeds, generated code calls `ctx.MarkSaved(this)` using whatever id the variant currently holds.
4. If the substrate matched an existing `(in, out)` row whose id is different from the variant’s current id, the session records an entity id that does not correspond to the row that actually exists.

Concrete replay:

```csharp
// Existing row is the canonical deterministic id D for (a, touches, b).
await session.SaveAsync(new CrossLink { Source = a, Target = b }, tx);

// Later, caller assigns a different id X for the same endpoints.
await session.SaveAsync(new CrossLink { Id = X, Source = a, Target = b }, tx);

// UNIQUE(in,out) updates the existing row D; MarkSaved tracks X.
```

The reverse order has the same problem: first save with assigned/random id `X`, later save without assignment derives `D`; duplicate update hits `X`, while `MarkSaved` tracks `D`.

**Fix shape:** relation variant identity should be canonical. I would remove user-assigned ids from variants entirely: reject `[Id]` on relation variants with a diagnostic, always derive from `(in, edge, out)`, and make `__MintId()` throw when endpoints are not yet resolvable instead of falling back to a random id. If you really want explicit edge ids later, that needs a different duplicate policy; with `UNIQUE(in,out)`, arbitrary ids and replay-replace semantics are at odds.

### 2. The documented hydration flow calls `session.GetAll<T>()`, but the runtime does not expose it — M

The Hydration terminal docs show the intended `IdsAsync -> Hydrate -> mutate -> save` flow as:

```csharp
foreach (var c in session.GetAll<Constraint>())
    c.Description = c.Description + " (reviewed)";
```

The API reference repeats the same shape for `CodeSymbol` hydration. I could not find a `GetAll<T>()` implementation in `SurrealSession`; the public read surface exposes `Get<T>(IRecordId)` and navigation/edge queries. That makes the documented hydration examples uncompilable and weakens the usefulness of `Workspace.Hydrate.{Table}(ids)`, because it returns only a session, not the hydrated root list.

Users can work around it by keeping the original id list and calling `session.Get<T>(id)` for each id, but that is not the documented API and it is clumsy for the exact batch-mutate flow this feature exists to support.

**Fix shape:** add:

```csharp
public IReadOnlyCollection<T> GetAll<T>() where T : class, IEntity
```

filtering the identity map by `T`, with `ThrowIfClosed()` and deterministic ordering if you care about stable test output. Alternatively, change the docs to iterate the original id list and call `Get<T>(id)`, but adding `GetAll<T>()` is the better API.

### 3. SurrealQL identifier quoting is still an open correctness risk — M / live-validation needed

Identifiers are still emitted bare. `SurrealFormatter.Identifier()` only validates the regex `\A[A-Za-z_][A-Za-z0-9_]*\z`; it does not quote or reject SurrealQL keywords/literals. The schema emitter also concatenates table, field, index, and edge names directly into DDL strings.

That means a C# property/type/relation name that snake-cases to something meaningful to the SurrealQL parser — for example `None`, `Order`, `Group`, or similar — can produce syntactically valid-looking but semantically wrong or parser-rejected SQL. `Improvements.md` already tracks this as needing live SurrealDB validation, and I agree with that classification.

**Fix shape:** centralize identifier rendering into a single quote-aware function and use it from both query compilation and schema emission. Backtick-quote every generated identifier unless live validation proves a smaller reserved-word table is safer. This will change generated SQL snapshots, so do it deliberately and with live substrate coverage.

### 4. Stale docs/comments still describe old session and projection shapes — L

A few docs/comments are now behind the code:

- `SurrealSession.SaveAsync` XML still says the session closes on return regardless of outcome and talks about a streamed server-side transaction/commit boundary. The current save method dispatches through the caller’s existing `SurrealTransaction`, closes only on failure, and does not commit.
- Projection comments still refer to a JSON-backed row in places; the current implementation uses `SurrealObjectValue` / `ValueProjectionRow`.
- README says packages are not yet published, while quickstart shows `PackageReference` snippets using `0.1.0-preview.*`. That can be fine as aspirational guidance, but today it reads like a contradiction.

These are not runtime blockers, but stale docs in a source-generator persistence library are not harmless. Users debug generated systems by reading comments and examples; if those lie, they will waste time.

---

## Public API fit

The public shape is still coherent for the narrow audience: SurrealDB + C# + aggregate-shaped models + explicit transaction ownership + source-generated persistence. The project should keep describing itself that way. Do not market it as a general ORM.

The new validation and fail-closed contracts make the library much more credible than the earlier branch. The remaining code blocker is relation-variant identity. Fix that and the main branch looks like a serious preview rather than a prototype with nice architecture.

## Recommended next work

1. Fix relation variant identity: disallow/ignore user-assigned variant ids and remove the random fallback from `__MintId()`.
2. Add `SurrealSession.GetAll<T>()` or rewrite the hydration docs/examples around `Get<T>(id)`.
3. Live-validate identifier quoting and apply a single quoting policy across query and DDL emission.
4. Clean stale XML/docs comments so the public mental model matches the runtime.

After item 1, I would be comfortable calling the main branch a focused, useful preview for its intended niche. Without item 1, relation variants still have a sharp edge exactly where identity matters most.