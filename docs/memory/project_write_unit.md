---
name: Writes are property setters (sync) and async dispatch methods (Save/Delete/Relate/Unrelate)
description: [Property]/[Parent]/[Reference] are property-only attributes — write surface is { get; set; }, pure backing-field setters with no Session interaction (except [Parent] cascade-track via AdoptIfUnbound). Edge mutations go through async Session.RelateAsync<TKind> against the user's SurrealTransaction. No auto-emitted Add/Remove/Clear methods. Domain methods are plain user code.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
**Scalar member writes** — `[Property]`, `[Parent]`, optional `[Reference]` — emit as **pure property setters**. Roslyn enforces the property shape: those attributes target `AttributeTargets.Property` only. The user declares `partial T Name { get; set; }` and the generator emits `set => _name = value;` (literally a backing-field write, no `__WriteField`, no `_session.SetField`, no buffer). The session has no idea the write happened — entity state lives in the entity, full stop. SaveAsync at dispatch time reads each property via the backing field and builds the typed CBOR content.

The only side effect on a setter is `[Parent]`: the emitted setter additionally calls `((IEntity)value).Session?.AdoptIfUnbound(this)` so a freshly constructed `new Constraint { Design = design }` joins design's session and shows up in `design.Constraints` at Save time.

Mandatory non-nullable `[Reference]` stays getter-only with an `OnCreate{Name}` simple-form `partial void` hook that `IEntity.Initialize` runs during `Track`.

**Relations** go through async dispatch only — `Session.RelateAsync<TKind>` and `Session.UnrelateAsync<TKind>` (with `IRecordId` and `IEntity` overloads, plus optional explicit edge id and/or payload dictionary). Each call dispatches against the user-supplied `SurrealTransaction` immediately — no buffer, no snapshot diff, no SaveAsync drain. The forward kind's marker class (`Restricts : IRelationKind`, emitted by `RelationKindEmitter`) carries the SurrealDB edge name as a static abstract property.

The dispatch shape is `tx.UpsertAsync(edge.ToSdk(), { in: src, out: tgt, …payload })` — typed CBOR, no SurrealQL string. With the default Idempotent edge id (deterministic hash of `src|table|tgt`) plus the schema's `UNIQUE INDEX (in, out)`, re-running the same triple lands on the same row in place of tripping the index.

**There are no auto-emitted Add/Remove/Clear methods** — the user writes a one-line async passthrough if they want a domain verb:

```csharp
public Task RestrictsAsync(IRestrictedBy x, SurrealTransaction tx, CancellationToken ct = default)
    => Session.RelateAsync<Restricts>(this, x, tx, ct);
```

Edge sync sites that need to happen during entity construction (before tx exists) buffer pairs into a small list and dispatch them after `await session.SaveAsync(root, tx)` — see `Disruptor.Surface.Sample/Program.cs` for the canonical pattern.

**Multi-field bundled writers and any other domain methods are plain user code.** The framework recognises NO method-name conventions. Pairs with `project_method_names_are_inert.md`.

**Why:** Property setters read more naturally than method calls; pure backing-field setters mean entity state has one home (no buffer / live-dict drift). The async-only relation surface acknowledges that edge writes need a transaction — there's no honest "buffer now, dispatch later" mode that doesn't lie about durability. Auto-emitted Add/Remove/Clear were dropped 2026-04 once the typed surface landed; sync `Relate` was dropped preview.45 once the buffer it required was deleted.

**How to apply:** When validating generator output: scalar `[Property]`/`[Parent]`/`[Reference]` write surface is `{ get; set; }` with pure backing-field bodies. Relation collections are read-only; mutations go through `Session.RelateAsync<TKind>(...)` against a tx. There should be no `protected void Add{Name}` / `Remove{Name}` / `Clear{Name}` methods, no `__WriteField` helper, no sync `Relate` overloads in `.g.cs` files. User-side passthroughs are async one-liners taking a `SurrealTransaction`.
