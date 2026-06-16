---
name: Step 0–5 plan to delegate progressively to native SurrealDB primitives — DONE 2026-05-12
description: HISTORICAL. Staged plan to delegate concurrency/atomicity/reads to SurrealDB. Closed 2026-05-12 — Steps 0–3 shipped preview.29–.34, Step 4 PARKED by design (load-through-txn covers cross-session coherence; sync in-session reads stay), Step 5 (cascade re-anchor) is a feature carry-over not architectural debt. preview.40–.46 ate the "remaining polish" list. Read for journey context, not action items.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
The current design's unease (see `project_substrate_shadowing.md`) is escapable as a sequence, not a rewrite. Each step is independently valuable.

**Step 0 — Own the wire.** Build a minimalist 1:1 SurrealDB client. **First pass shipped 2026-05-10** as `Disruptor.Surreal` at sibling repo `../surrealdb-dotnet` — see `project_disruptor_surreal_client.md`. Scope as decided: CBOR over WebSocket RPC only (HTTP deferred, embedded explicitly out, JSON wire format out). Auth, bindings, txn-id propagation, typed exception hierarchy, server version pin to v3 — all in. De-risks every later step; stops paying the SurrealDb.Net "second-cousin" tax (see `project_dotnet_sdk_risk.md`).

**Step 1 — Streamed commit.** ✅ **Shipped 2026-05-10 in preview.29.** `CommitAsync(Surreal db)` opens a server-side txn via the SDK, dispatches each rendered command as its own RPC inside the txn, then commits. BEGIN/COMMIT come from the SDK txn handle; per-statement dispatch eliminates the wire-side batch-size cliff. `SurrealCommandEmitter.EmitOne` exposes per-command rendering; the multi-command `Emit` is preserved for diagnostics.

**Step 2 — WriterLease → native txn-conflict.** ✅ **Shipped 2026-05-10 in preview.29.** WriterLease.cs deleted; writer_lease schema chunk removed; AcquireWriterAsync emission removed from CompositionRootEmitter; lease parameter removed from LoadEntryEmitter and HydrationQuery. Conflicts surface as `Disruptor.Surreal.SurrealConflictException` from `tx.CommitAsync`. Domain catches and reloads.

**Step 3 — Explicit Save into an app-owned Transaction.** ✅ **Shipped across preview.30–.34.** Two reframes converged on the way in:
1. First reframe: dropped the "eager flush" misframe; replaced with explicit `Save` (sync setters preserved naturally, error locality at Save, PendingState/CommitPlanner shrink).
2. Second reframe: the library doesn't own the txn lifecycle. The SDK's `Transaction` is the unit; the library is one consumer. App opens, app commits.

**Shipped (preview.30–.35):**
- preview.30: app-owned `Transaction`; `SurrealSession.SaveAsync(Transaction)` flush-all replaces `CommitAsync`; loaders gain `Surreal db` + `Transaction tx` overloads.
- preview.31: per-entity `SurrealSession.SaveAsync(IEntity, Transaction)` via generator emission; `ISaveContext` orchestration; auto-bind via `EnsureBoundForSave`; "in DB" semantics for `IsTracked`.
- preview.32: children recursion + `DeleteAsync(IEntity, Transaction)` + `RelateAsync<TKind>` + `UnrelateAsync<TKind>`; `GetParentOrDefault`.
- preview.33: relations dispatch in emitted `SaveAsync` (snapshot diff via `GetNewOutgoingEdges<TKind>`); Sample on per-entity Save throughout; flush-all `SaveAsync(Transaction)` deleted.
- preview.34: `CommitPlanner` deleted; `RenderBatch` deleted; legacy sync `Delete` deleted (cascade temporarily missing).
- preview.35: `PendingState` deleted; `CommandLog` extracted to its own file; `Pending.X` calls removed from setters / hydration sink / Record; `HasPendingWrite` re-implemented over a small per-(owner, field) HashSet.

**Remaining incremental polish (no longer dead-weight, just shape decisions):**
- `__WriteField` + `_pendingWrites` setter-buffering emit. Currently still routes through `Session.SetField` (which now just updates state dicts). Could be inlined per-setter to drop the helper; ergonomic value of `new T { Reference = otherEntity }` pre-bind buffering needs to stay.
- Sync `Session.Relate<TKind>` / `Unrelate<TKind>` overloads. Used by user-code domain verbs (`constraint.Restricts(userStory)`); now updates state.Edges only. Could be replaced with async equivalents if domain verbs are reshaped to be async.
- `IEntity.Bind` / `Flush` / `Initialize` / `MarkAllSlicesLoaded` hooks. All still meaningful; would only go alongside a setter-emit redesign.
- Cascade re-anchor (lost in preview.34): re-implement `[Reject]`/`[Cascade]`/`[Unset]` semantics against the loaded snapshot for `DeleteAsync` so single-call deletes resolve incoming references correctly.

**Lines removed across the unpainting (preview.29 – .35):** writer_lease + transport assemblies + WriterLeaseTests + CommitPlanner (~360 lines) + CommitPlannerTests (~880 lines) + PendingState (~390 lines) + PendingStateTests (~250 lines) + various removals. Net negative four figures.

**The model:**
- App calls `db.BeginTransactionAsync()` to get a `Transaction`.
- App passes `Transaction` into `LoadAsync` to enter write-mode (or passes `db` for read-only).
- Properties / Relate / Delete sync, in-memory only.
- `session.SaveAsync(thing, ct)` — async; dispatches commands into the attached txn. User chooses what to save (no general "save everything" auto-magic; relieves us of change tracking).
- App calls `tx.CommitAsync()` or `tx.CancelAsync()`.

**Wins beyond the first reframe:**
- **Cross-session composition** — multiple sessions / multiple aggregates / mixed raw SDK calls all inside one app-managed txn. Atomicity is whatever the app decides.
- **Load-through-txn** — `LoadAsync(tx, …)` runs the load query inside the txn, so cross-session in-txn writes are visible. The most useful chunk of Step 4 (cross-session read coherence) lands "for free" without changing reads-from-snapshot semantics.
- **No change tracking** — `SaveAsync(thing)` lets the user choose what to save; we just dispatch the entity's current state into the txn. No per-field-per-record dirty machinery.

**What dies:** PendingState's per-field/per-record dirty tracking; CommitPlanner's phase ordering / dedup / collapse; Track-as-separate-verb (Save infers CREATE vs UPDATE from identity map); session-owned txn lifecycle.

**What survives:** identity map; loaded snapshot; reference-delete cascade planner (runs at delete-Save against snapshot); typed entity surface; generator / schema emit / typed ids / typed relations.

**Save semantics (decided 2026-05-10):**
- **Auto-recursive on Save.** `SaveAsync(entity)` walks the entity's reference graph and dispatches everything *new* (not in identity map) that's reachable, in dependency order. Forward refs (`[Reference]` / `[Inline]` / `[Parent]`) get CREATEd before the entity. New `[Children]` that the entity owns also dispatch. Existing entities (loaded from DB) only save when explicitly passed.
- **Whole-entity always.** Every save dispatches `CREATE/UPDATE record:id CONTENT { ...full state... }`. No per-field `SET`/`UNSET`. Loses disjoint-field concurrency, but native txn-conflict makes that moot — concurrent writers collide at COMMIT regardless of whether we sent SET or CONTENT.

**Implementation collapse:**
- Setters become *pure backing-field setters*. No `__WriteField`, no buffer, no Session interaction. C# properties go back to being C# properties.
- `PendingState` essentially disappears for entity field state. Save reads the entity's current state at dispatch time. Identity map is the only "is this in DB?" check.
- `SurrealCommandEmitter` collapses to mostly `CREATE/UPDATE … CONTENT { ... }`. No SET/UNSET statements.
- `CommitPlanner` collapses to two things: the recursive new-entity walker (dispatch order) and the cascade planner for deletes.
- Relations: `Relate<TKind>(c, s)` updates in-memory collection (sync). "New" is computed as snapshot-at-load vs. current state — cheap diff, no dirty flag. Save(c) dispatches RELATE for new outgoing relations. Same for Unrelate.
- Identity map gets seeded with new entities as Save walks the graph (otherwise a re-Save would re-INSERT them).

**Mechanism: generator-emitted, NOT reflection.** Per-entity `IEntity.SaveAsync(Transaction tx, ISaveContext ctx, ct)` is emitted by the generator alongside `Hydrate`/`Bind`/`Initialize`/`Flush`/`OnDeleting`. Body is direct typed code — typed field access, snake_case names baked at emit time, recursion delegated through `ctx.SaveAsync` callbacks back into Session. No runtime reflection, no GetValue/attribute lookup, no per-call introspection cost. This mirrors the rest of the IEntity surface — every other interaction with the entity is generator-emitted; Save should be too. **Default failure mode to watch for: reaching for reflection when the codegen infrastructure already has the type knowledge.**

**Other open points (deferrable):** whether `Transaction` is passed directly or wrapped in a "writer context" intermediate (start direct).

**Step 4 — Reads through txn with cache. PARKED.** Sync reads stay sync; load-through-txn (delivered in Step 3) covers cross-session in-txn read coherence. Step 4 only matters for *server-computed* mid-txn state (COMPUTED fields, triggers). Workload-driven; not on the critical path.

**Step 5 — Reference-delete planning re-anchored.** Cascade lost in preview.34 when CommitPlanner was deleted. To restore: re-implement `[Reject]`/`[Cascade]`/`[Unset]` semantics against the loaded snapshot's reference state for `DeleteAsync`. Algorithm survives; data source changes from `PendingState.References` to `state.References`. Standalone work; sequence-able anytime.

**Hydration → Value migration. ✅ Shipped 2026-05-10 in preview.37.** The JSON projection bridge (HydrationJson, SurrealResultSet, SurrealSdkTransport, ISurrealTransport) is gone. `IEntity.Hydrate(Value, IHydrationSink)` is the new contract; `HydrationValue` provides Value-native helpers. Generator emits Value-consuming bodies end-to-end (PartialEmitter, AggregateLoaderEmitter, IdsAsyncEmitter, TraversalBuilderEmitter, LoadEntryEmitter). Loaders/queries take Surreal/Transaction directly with no bridge. ~640 lines of bridge removed.

**Architectural pivot status: COMPLETE (preview.29 → .37).** The library is the thin layer over `Disruptor.Surreal` that the original "shadowing the substrate" framing called for. Total deletion across the journey: ~5000 lines. Remaining work is feature-level (cascade re-anchor, more tests, docs sweep) — no architectural debt left to discharge.

**Plan-closure post-script (2026-05-12).** Three follow-on previews finished the "remaining incremental polish" list:
- preview.40 — pure backing-field setters; `__WriteField` + `_pendingWrites` setter buffering deleted (the first item on the list).
- preview.45 — sync `Session.Relate<TKind>` / `Unrelate<TKind>` deleted (the second item). Edge writes are async-only via `RelateAsync<TKind>` against the user's tx; the relation snapshot-diff (`GetNewOutgoingEdges<TKind>`, `relationsAtStart`, `PendingEdge`) deleted with it. The emitted `SaveAsync` no longer dispatches relations.
- preview.46 — `RelateAsync` switched from `RELATE` to `tx.UpsertAsync(edge.ToSdk(), content)`. With the Idempotent edge id (deterministic hash from src|table|tgt) plus the schema's `UNIQUE INDEX (in, out)`, re-runs land on the same row in place of tripping the index. Idempotence is owned by the substrate, not by application-level "loaded at start" tracking.

Plus reaches beyond the original list: preview.42 (typed-CBOR end-to-end, JSON gone), preview.41 (SurrealArray + reflection in HydrationValue gone), preview.43 (Surface* prefix on Query types).

**Open items as of plan closure:**
- ~~Step 5 (cascade re-anchor for `[Reject]`/`[Cascade]`/`[Unset]` semantics in `DeleteAsync`)~~ — **shipped preview.47 (2026-05-12)**. DeleteAsync now runs three-phase pre-flight resolve over `state.Entities` + `IReferenceRegistry`, predicts the cascade set, throws `CascadeRejectException` before wire dispatch on steady-state blockers, fires `OnDeleting` on every cascaded entity, mirrors Unset actions via `IEntity.SetReferenceTo`. See `project_reference_state.md`.
- The in-session read snapshot (`state.Entities`/`state.Edges`/`state.LoadedSlices`) survives **deliberately** as a post-load read cache so aggregate dot-walks stay sync. Not deferred Step 4 work — explicit decision (2026-05-12).

**What stays across the journey:** generator (modeling, schema emit, typed relations, typed ids), aggregate as preload-hint (not concurrency boundary), `Load{Root}Async` entry shape, identity map, reference-delete *algorithm*. The user-facing attribute surface is corner-independent.

**What shrinks:** `PendingState` (DB *is* the pending state, after Step 3), `CommitPlanner` (DB ordering inside txn absorbs most of it, after Step 3), `WriterLease` (✅ gone in preview.29).

**Bridges (transitional):** `SurrealSdkTransport` wraps the SDK to satisfy the legacy `ISurrealTransport`/`JsonElement` contract that hydration still consumes. Deletes alongside Step 4 when hydration migrates to `Value`.

**How to apply:** When implementing toward this direction, sequence work along these steps; don't skip ahead past dependencies. Stopping at Step 3 is itself a coherent corner (substrate handles concurrency/atomicity, sync reads survive). Step 4 is a deliberate commitment to a thinner library, not a refactor.

**Verification gates:** before Step 1, confirm v3 RPC's BEGIN→txn-id behavior empirically. Before Step 2, confirm native txn-conflict surfaces cleanly through the wire layer.
