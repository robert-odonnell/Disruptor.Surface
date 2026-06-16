---
name: [Inline] carves owned-sidecar references out of plain [Reference]
description: `[Reference]` alone is a foreign pointer — id-only at load time. `[Reference, Inline]` marks an owned/compositional sidecar — the loader emits `field.*` and hydrates the linked record alongside its owner.
type: project
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
`[Reference]` used to inline-expand the target unconditionally via `field.*` in the aggregate loader's projection. That blurred aggregate boundaries: a foreign-pointer-shaped reference (cross-aggregate, "I just want the id") was indistinguishable from an owned-sidecar (Details, "load it with me as part of my hydration"). 2026-04 introduced `[Inline]` as the explicit carve-out.

**Today's contract:**
- `[Reference]` alone → id-only at load time. `GetReferenceOrDefault<T>` returns null until the referenced record is loaded by some other path (typically a separate aggregate load).
- `[Reference, Inline]` → loader's nested `SELECT` adds `field.*`; `HydrationJson.HydrateReference<T>` notices the inline-record shape (`{ id, …content }`), constructs a fresh `T`, runs its `Hydrate`. Subsequent reads resolve to the fully-populated entity.

In the Sample schema all twelve `Details` references are tagged `[Reference, Cascade, Inline]` because Details IS an owned sidecar in this model. Most cross-aggregate references (`Concerns`, `References`, `Revises`, `Assesses`) are id-typed `IReadOnlyCollection<IRecordId>` and so don't go through this code path at all.

**Why:** Two distinct concepts were sharing one attribute. Foreign pointer vs. owned component is a structural distinction the schema cares about: foreign pointers are how you cite something outside your aggregate, owned components are part of your aggregate's load. Conflating them meant every reference fetched its target's row, which (a) wasted bytes for foreign refs the user didn't intend to read, and (b) muddied the aggregate boundary.

**How to apply:**
- New schemas: tag `[Inline]` only on the sidecar-style references where the linked record really IS part of this aggregate's loaded state. Default is foreign pointer, you opt into inlining.
- Generator: `AggregateLoaderEmitter.InlineReferenceFieldNames` filters on `p.IsInline`. `PropertyModel.IsInline` is set by `TableExtractor` when the user's [Reference] property also carries `[Inline]`.
- Future direction (#6 in the punch list): `[Inline]` is the seed for a richer ownership model — could grow `[Owned]` / `[Part]` distinctions later if needed; right now `[Inline]` is the single knob.
