---
name: Generated aggregate loader and IEntity.Hydrate (typed CBOR / SurrealValue)
description: Per-aggregate Load{Root}Async grafted onto the user's [CompositionRoot] partial. The loader issues a single nested SurrealQL SELECT through the SDK and feeds the typed SurrealValue tree into IEntity.Hydrate, which populates backing fields and re-hydrates inline-expanded references via HydrationValue helpers.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
Snapshot hydration is generator-emitted, not hand-coded. Three pieces fit together (current shape post-preview.42):

1. **User's `[CompositionRoot]` partial** — exactly one class per compilation tagged `[CompositionRoot]`, declared `partial`. The generator grafts two `Load{Root}Async` overloads per `[AggregateRoot]`:
   - `Load{Root}Async(SurrealClient db, {Root}Id rootId, ct)` — read-only.
   - `Load{Root}Async(SurrealTransaction tx, {Root}Id rootId, ct)` — write-mode, query runs inside the txn so cross-session in-txn writes are visible.
   No ctor / fields / base class are emitted; the user owns construction. Diagnostics: CG018 (multiple `[CompositionRoot]`), CG019 (not partial).

2. **`{Root}AggregateLoader` (internal static)** — emitted by `AggregateLoaderEmitter`. Issues a SINGLE nested-SELECT through `tx.QueryAsync(sql, bindings, ct)` where the root id flows in as a typed `SurrealRecordIdValue` binding (`$_rootId`):
   - Root row pulls `*` plus `field.*` inline expansion for each `[Reference, Inline]` field.
   - One subselect per non-root member, scoped via the dotted parent path back to the root: `WHERE feature.epic.design = $parent.id`. Each subselect inlines its own `[Reference, Inline]` fields.
   - One subselect per relation kind that touches this aggregate. Within-aggregate + source-side cross-aggregate scope by `in.<source-path> = $parent.id`; target-side cross-aggregate scope by an OR over the distinct target paths.
   - Why one query: separate per-table loaders had failure modes (full-table SELECTs, edge filters that pulled every aggregate's edges). `$parent.id` scoping inside nested subselects fixes both.
   - Response handling: `ExtractFirstResultRow` calls `EnsureSuccess()` first, so a SurrealDB statement-level error throws `SurrealRpcException` instead of silently producing no rows (preview.44).

3. **`IEntity.Hydrate(SurrealValue, IHydrationSink)`** — emitted on every entity partial. Walks each declared property using `HydrationValue` helpers (Value-native, no JSON, no reflection):
   - `[Id]` → `_id = new {Name}Id(HydrationValue.ReadRecordId(row, "id").Value)`.
   - scalar `[Property]` → `HydrationValue.ReadString` for strings, `HydrationValue.ReadOrDefault<T>` for other scalars / arrays / records via the typed CBOR converter.
   - `[Property] IReadOnlyList<TElement>` (element collections) → typed list build through HydrationValue with per-element record/scalar handling (replaced SurrealArray, preview.41).
   - `[Reference]` → `HydrationValue.TryReadReferenceId` for plain references (id-only); `HydrationValue.HydrateInlineReference<T>` for `[Reference, Inline]` (constructs `new T()` and runs its `IEntity.Hydrate` on the embedded payload, also tracks the link via the hydration sink).
   - `[Parent]` → backing field set via `HydrationValue.TryReadReferenceId` (parent id only; entity may or may not be in the same aggregate snapshot).
   - `[Children]` and forward/inverse relations are NOT read here — children are computed via the parent backing-field reverse lookup; edges are loaded by the per-aggregate loader's edge subselects which feed the session's edge index via `IHydrationSink.Edge`.
   - Each property hydration ends with a `MarkSliceLoaded(this.Id, "fieldName")` call so generator-emitted strict reads can distinguish "loaded but null" from "never loaded".

The session exposes explicit-interface `IHydrationSink` methods (`Track` / `Edge` / `MarkSliceLoaded`) that the emitted Hydrate paths call. `Track` calls `entity.Bind(this)` once per first-seen entity so post-load setter calls work; duplicate-instance Track during include-heavy queries is silently absorbed (the emitted hydrator's `new T() + Hydrate` pattern produces fresh instances per row, but the identity map already has one).

**Why:** Generator-emitted loader keeps the per-aggregate query shape locked to the schema (no hand-maintained query strings drifting). `IEntity.Hydrate` lets the loader stay aggregate-agnostic in its hot path. Inline-reference re-hydration solves the loader-vs-`Get<T>` impedance mismatch the obvious way (an inline-expanded reference becomes a fully-hydrated entity in the snapshot).

**How to apply:** When adding a new `[Table]` the generator picks it up. When adding a new scalar `[Property]` type, extend `HydrationValue.ConvertValue` (typed switch — no reflection). When adding a new edge kind, the loader emits the right fetch automatically based on the cross-aggregate flag from `ModelGraph`. When adding a new `[Reference]` shape, check `EmitHydrateReference` and `HydrationValue.HydrateInlineReference<T>` together.
