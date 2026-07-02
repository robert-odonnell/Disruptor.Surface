# Code review — 2026-07-02 (main / `master`)

Scope: current default branch (`master`) after the 2026-07-02 review-and-fix pass. This is a source review of the generator pipeline, emitted contracts, runtime session boundary, relation-variant persistence, query layer, and docs.

This pass is intentionally aligned against [`docs/remaining-work.md`](remaining-work.md) and [`../Improvements.md`](../Improvements.md). Findings below exclude backlog items already captured there: live-substrate validation, SurrealQL identifier quoting, deterministic-edge-id smoke against pre-existing data, union semantic resolution, AOT/trim, query/delete feature backlog, ecosystem packages, and per-element emit granularity.

Severity: **H** = can corrupt persisted/session identity or break users in a normal flow; **M** = real defect with plausible trigger; **L** = docs / DX / latent robustness.

## Verdict

Main is materially stronger than the reviewed branch. The earlier high-risk session-boundary issues are fixed: `FetchAsync` is now pinned to an already-tracked root, foreign root rows are rejected fail-closed, `ExecuteIntoSessionAsync` closes a supplied session on hydration/wire failure, nullable relation payload duplicate-update variables bind explicitly, and relation variants derive deterministic edge ids for the normal endpoint-first save path.

After removing already-planned work from the review, two uncaptured issues remain worth acting on: one release-blocking relation-variant identity hole, and one API/docs mismatch in the hydration workflow.

## Findings not already covered by the backlog

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

This is distinct from `remaining-work.md`'s pre-existing-data migration question. That item is about rows written before deterministic ids existed. This finding is about fresh code paths in the current branch that can still create non-canonical ids.

**Fix shape:** relation variant identity should be canonical. I would remove user-assigned ids from variants entirely: reject `[Id]` on relation variants with a diagnostic, always derive from `(in, edge, out)`, and make `__MintId()` throw when endpoints are not yet resolvable instead of falling back to a random id. If explicit edge ids are needed later, that needs a different duplicate policy; with `UNIQUE(in,out)`, arbitrary ids and replay-replace semantics are at odds.

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

filtering the identity map by `T`, with `ThrowIfClosed()` and deterministic ordering if stable output matters. Alternatively, change the docs to iterate the original id list and call `Get<T>(id)`, but adding `GetAll<T>()` is the better API.

### 3. Stale docs/comments still describe old session and projection shapes — L

A few docs/comments are now behind the code:

- `SurrealSession.SaveAsync` XML still says the session closes on return regardless of outcome and talks about a streamed server-side transaction/commit boundary. The current save method dispatches through the caller’s existing `SurrealTransaction`, closes only on failure, and does not commit.
- Projection comments still refer to a JSON-backed row in places; the current implementation uses `SurrealObjectValue` / `ValueProjectionRow`.
- README says packages are not yet published, while quickstart shows `PackageReference` snippets using `0.1.0-preview.*`. That can be fine as aspirational guidance, but today it reads like a contradiction.

These are not runtime blockers, but stale docs in a source-generator persistence library are not harmless. Users debug generated systems by reading comments and examples; if those lie, they will waste time.

## Public API fit

The public shape is still coherent for the narrow audience: SurrealDB + C# + aggregate-shaped models + explicit transaction ownership + source-generated persistence. The project should keep describing itself that way. Do not market it as a general ORM.

The new validation and fail-closed contracts make the library much more credible than the earlier branch. The remaining uncaptured code blocker is relation-variant identity. Fix that and the main branch looks like a serious preview rather than a prototype with nice architecture.

## Recommended next work from this review

1. Fix relation variant identity: disallow/ignore user-assigned variant ids and remove the random fallback from `__MintId()`.
2. Add `SurrealSession.GetAll<T>()` or rewrite the hydration docs/examples around `Get<T>(id)`.
3. Clean stale XML/docs comments so the public mental model matches the runtime.

Everything else I saw that matters is already in `docs/remaining-work.md` or `Improvements.md`, so I am not duplicating it here.