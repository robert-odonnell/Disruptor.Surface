---
name: Sync read-side, async dispatch boundary
description: Entity reads (properties, relation accessors, [Children]) are sync against the in-memory snapshot. Async surface is the dispatch boundary: SaveAsync, DeleteAsync, UnrelateAsync, FetchAsync, plus Workspace.Load{Root}Async and Workspace.Query.{Table}.{ExecuteAsync,IdsAsync,LoadAsync}. Sync property setters / Track / AdoptIfUnbound stay sync (pure in-memory). Generator code that emits Task-returning property reads is wrong.
type: feedback
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
Inside the entity model every read is a sync property — `[Property]`, `[Reference]`, `[Parent]`, `[Children]`, relation accessors. They resolve off the in-memory snapshot (entity backing fields plus session's identity map / edge index). Properties can throw `LoadShapeViolationException` when a slice wasn't hydrated, but they're still sync.

**The async surface is the dispatch boundary** — anything that talks to the substrate:
- `SurrealSession.SaveAsync(IEntity, SurrealTransaction, ct)` — per-entity Save.
- `SurrealSession.DeleteAsync(IEntity, SurrealTransaction, ct)` — entity DELETE.
- `SurrealSession.SaveAsync(new TVariant { Source, Target }, tx)` — edge creation; `UnrelateAsync<TKind>(...)` — edge deletion.
- `SurrealSession.FetchAsync(query, db|tx, ct)` — partial-merge top-up hydrate.
- User's `[CompositionRoot]` partial's `Load{Root}Async(db|tx, id, ct)` — initial aggregate hydrate.
- `Workspace.Query.{Table}(...)` terminals — `ExecuteAsync` / `IdsAsync` / `LoadAsync` / projection's `Select(...).ExecuteAsync`.
- `Workspace.ApplySchemaAsync(db|tx, ct)` — schema bootstrap.

**Sync surface that talks to the session** (not the substrate, no Task):
- `Session.Track<T>(entity)` — register a fresh entity in the identity map; runs Initialize idempotently.
- `Session.AdoptIfUnbound(child)` — cascade-track from emitted `[Parent]` setters.
- `Session.Get<T>(id)` / `IsTracked(id)` / `IsSliceLoaded(...)` / `QueryChildren<T>(...)` / `QueryOutgoing<T>(...)` / `QueryIncoming<T>(...)` / `QueryRelatedIds<TKind>(...)` / `QueryInverseRelatedIds<TKind>(...)` — reads against the in-memory snapshot.
- Property setters — pure backing-field writes (preview.40).

**Why:** Reads come from the in-memory snapshot populated at load time, so async getters were always `Task.FromResult` over a Dictionary lookup, i.e. fictional async. Wrapping all of that in `Task` misled callers about cost and made user code awkward (`await (await x.GetParentAsync()).GetDetailsAsync()` everywhere). The canonical shape is "load aggregate (async), sync access on the loaded snapshot, async dispatch when modifying the substrate." This is the post-preview-45 model — sync `Relate`/`Unrelate`/`CommitAsync` are gone.

**How to apply:** Generator emission for read-side properties / accessors must return the bare type, not `Task<T>`. Edge writes must be async + take a `SurrealTransaction`. Property setters are sync void with pure backing-field bodies. Domain verbs that wrap edge writes are async one-liners taking `SurrealTransaction`.
