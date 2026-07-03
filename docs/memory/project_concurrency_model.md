---
name: SurrealSession concurrency model — substrate-owned
description: SurrealSession is an in-memory snapshot (identity map + edge index + load-shape tracker) bolted onto an app-owned SurrealTransaction. No application-level lease, no application-level conflict detection — concurrent writers collide at COMMIT as native SurrealConflictException from the SDK. Single-shot lifecycle: load → mutate → commit (or fail), then loop.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
The session knows nothing about read/write permissions. Domain code mutates freely (property setters, Track, AdoptIfUnbound). Edge creation (`SaveAsync` against a relation-variant entity), edge deletion (`UnrelateAsync`), and entity writes (`SaveAsync` / `DeleteAsync`) dispatch immediately against the user-supplied `SurrealTransaction` — no buffered "dirty batch", no `CommitAsync` on the session.

**Lifecycle:**
- App calls `db.BeginTransactionAsync()` to get a `SurrealTransaction`.
- App passes the transaction into `LoadAsync`/`SaveAsync`/`DeleteAsync`/`RelateAsync` etc.
- Reads from already-loaded entity-graph navigation are sync, off the in-memory snapshot.
- Reads via `Workspace.Query.{Table}(...).ExecuteAsync(tx)` go through the transaction (see in-txn writes).
- App calls `tx.CommitAsync()` (or `tx.CancelAsync()`).

**Concurrency surfaces natively.** Two writers operating against the same row pattern → second commit raises `SurrealConflictException` from the SDK at `tx.CommitAsync()`. The session does not pre-check or pre-coordinate. Recovery is the app's: catch, reload, replay, retry.

**Failure semantics:** any exception during a session-side dispatch (`SaveAsync`/`DeleteAsync`/`RelateAsync`/`UnrelateAsync`) marks the session closed (`IsClosed = true`); subsequent calls throw `InvalidOperationException`. The transaction handle is the app's to commit-or-cancel. Pairs with `feedback_one_catcher_many_throwers.md`.

**What the session still tracks in memory** (deliberately, for sync read coherence after load):
- `state.Entities` — identity map.
- `state.Edges` — read-side edge index (HashSet of `(src, edge_table, tgt)`); populated by hydration sink + RelateAsync.
- `state.LoadedSlices` — per-entity load-shape (which fields/relations were hydrated; gates strict reads).
- `loadedAtStart` — entity ids loaded at hydration time, so `IsTracked(id)` distinguishes "in DB" from "constructed in this session" (drives CREATE vs UPSERT in emitted SaveAsync).

This snapshot is a deliberate post-load read cache, not deferred unpainting work — confirmed 2026-05-12.

**How to apply:** When editing session methods, do not reintroduce a write buffer / dirty batch / commit-time flush — every write must go through the user's tx synchronously-as-it-happens. Don't add lease / sequence-counter / lock primitives — the substrate owns conflict detection. When testing concurrent-writer behaviour, set up an actual second transaction and assert on `SurrealConflictException` at commit, not on session-internal state.

**Naming history:** `Workspace` runtime type → `SurrealSession`; the user-facing "Workspace" name now belongs to the `[CompositionRoot]` partial that the user owns. WriterLease deleted preview.29; CommitPlanner deleted preview.34; PendingState deleted preview.35; sync Relate / state.Edges-as-write-buffer deleted preview.45.
