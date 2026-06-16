---
name: SurrealFormatter — only Identifier() validation remains
description: Trimmed to a single Identifier() extension method that regex-validates table / field / edge / slice names before they're inlined into emitted SurrealQL. RecordId / StringLiteral / RenderSurrealLiteral all deleted preview.42 once the wire path went typed-CBOR; user values now flow as typed SurrealValue bindings, never as formatted SurrealQL text.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
`Surface.Runtime/SurrealFormatter.cs` is now a tiny defensive layer:
- `Identifier(this string name)` — validates `[A-Za-z_][A-Za-z0-9_]*`; throws `SurrealFormatException` on invalid (always a generator/emitter bug, never user input).
- `SurrealFormatException` — the throw type.

That's it. Everything else that used to live here (`RenderSurrealLiteral`, `StringLiteral`, the `RecordId(id)` formatter, the `IDictionary` content-render case, JSON fallback) was deleted in preview.42 when the wire path went end-to-end typed CBOR.

**Where Identifier() is called:**
- `SurfaceQueryCompiler` — table names, field names, edge names that are inlined into `SELECT … FROM <table>` / `WHERE <field> = $_pN` / etc.
- `SchemaEmitter` — DDL chunks (the only place that genuinely builds SurrealQL text since DDL is fundamentally text).
- `AggregateLoaderEmitter` — table / edge / slice-key inlines into the nested SELECT.
- `RelateAsyncCore` no longer goes through it — that path uses `tx.UpsertAsync(edge.ToSdk(), content)` (typed SDK call, no SurrealQL string).

**User values never go through any formatter.** Predicate operands, IN-list elements, pinned ids, RELATE endpoints, content fields — all flow as typed `SurrealValue` variants (`SurrealRecordIdValue`, `StringSurrealValue`, `SurrealNumberValue`, `SurrealListValue`, `SurrealObjectValue`, …) through `tx.QueryAsync(sql, bindings, ct)` or the SDK's typed methods (`CreateAsync` / `UpsertAsync` / `DeleteAsync`). Wrapping happens in `SurfaceQueryCompiler.WrapAsSurrealValue` or `ContentValue.Set*` — no string formatting in either.

**Why the trim:** the SurrealDB JSON-binding-doesn't-preserve-Thing-types problem (which originally drove `SurrealFormatter`'s existence) doesn't apply on the CBOR-over-WS path. CBOR-tagged record ids round-trip cleanly as Things. With typed bindings, the only thing left to validate is the regex-trusted set of identifiers we ourselves emit.

**How to apply:** Any new code path that builds emitted SurrealQL text — extremely rare now — must go through `Identifier()` for any name component, and must use typed bindings (not formatted literals) for any value. New value types needing wire support get a case in `SurfaceQueryCompiler.WrapAsSurrealValue` (predicate operands) and/or `ContentValue.Set` (entity content), not a new formatter helper.
