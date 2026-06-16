---
name: Typed id design
description: Per-table {Name}Id readonly record structs wrapping a validated string. Two forms: Ulid stringifications (auto-mint) or short lower_snake_case slugs (opt-in). Anything else throws.
type: project
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
Typed ids are emitted by the generator as `readonly record struct {Name}Id(string Value) : IRecordId` — one per `[Table]`. Struct (not class/record) so short-lived id values stay on the stack. `IRecordId` is a minimal interface: `string Table { get; }` and `string ToLiteral()`. The struct has an implicit conversion to the canonical `RecordId` so workspace internals (entities / parents / references / edges dictionaries) can key off one struct type while the user-facing API stays strongly typed.

**Value validation:** The primary-ctor `Value` parameter flows through `Disruptor.Surface.Runtime.RecordIdFormat.Validate(string)`, which accepts two and only two forms:
- **Ulid stringification** — exactly 26 chars of `[A-Z0-9]` (Crockford Base32). What `{Name}Id.New()` mints via `Ulid.NewUlid().ToString()`.
- **Short lower_snake_case slug** — starts with `[a-z]`, followed by `[a-z0-9_]*`, max 32 chars. Opt-in for stable-named records (singletons, config rows, well-known references). Short on purpose: if you're reaching for a 30-char slug, you probably want a Ulid.

Anything else throws `FormatException` at construction time. No quoted-string ids, no uppercase identifiers, no special characters, no long opaque strings — Surreal record-id semantics treat ids as *records*, not free-form strings, and the library holds that line at the typed-id ctor.

**Why pinned this way:** Ulid configurability (`[RecordIdValue<T>]`) was removed on 2026-04 — it was scaffolding for hypothetical demand. Then the question became "should slug-form ids be allowed at all?" — yes, but ONLY in a constrained shape that matches the surrounding lower_snake_case naming convention used for tables and fields. The validator is the choke point; even direct `new {Name}Id("anything")` from user code throws if the value isn't conformant.

**How to apply:**
- When emitting id types, generate `readonly record struct {Name}Id(string Value) : IRecordId` with a re-declared `Value { get; } = RecordIdFormat.Validate(Value)`.
- `New()` is the only mint path: `=> new(Ulid.NewUlid().ToString())`.
- Hydrate reads the raw string via `HydrationJson.ReadRecordId(__idElem).Value` and constructs the typed id (validator passes — DB-loaded values are trusted).
- The implicit conversion to `RecordId` is unconditional: `=> new(id.Table, id.Value)`.
- Session mutation methods are `Relate<TKind>(IRecordId, IRecordId)` / `Relate<TKind>(IEntity, IEntity)` — the typed-kind primitive subsumes the old string-keyed shape.
