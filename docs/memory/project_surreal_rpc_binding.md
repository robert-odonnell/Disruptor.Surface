---
name: SurrealDB /rpc JSON binding doesn't preserve Thing types — RESOLVED 2026-05-12
description: HISTORICAL. The original justification for inlining record ids into SurrealQL via SurrealFormatter rather than passing them through JSON-RPC vars. No longer relevant — the wire path moved to typed CBOR over WebSocket via Disruptor.Surreal (preview.29 SDK adoption, preview.42 typed-CBOR end-to-end). CBOR-tagged record ids round-trip as Things; the `params[1]` JSON binder gotcha doesn't apply.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
**Historical context (do not act on as live guidance).**

SurrealHttpClient used to send JSON-RPC 2.0 to `/rpc` (`{ "method": "query", "params": [sql, vars] }`), but bindings were inlined into the SQL via `SurrealFormatter` rather than passed in `params[1]`. The vars slot was sent as an empty object — required by the method signature.

**The problem this solved:** SurrealDB's `/rpc` JSON binder treated record-shaped objects (`{"tb": "...", "id": "..."}`) and `"table:value"` strings as generic `Object`/`Strand` values, not `Thing`s. A query like `WHERE id = $p0` with a record bound through JSON vars compared a Thing column against an Object → never matched → query returned zero rows with no error. SurrealQL literal syntax (`table:value`) was parsed at the query level and preserved type, so inlining via `SurrealFormatter.RecordId` was the only reliable way to bind record ids over HTTP.

**Why it's resolved:** preview.29 adopted `Disruptor.Surreal` (CBOR over WebSocket RPC). CBOR-tagged record ids round-trip as Things — the JSON binder gotcha doesn't apply on the CBOR path. preview.42 then took the wire path end-to-end typed: every value is now a `SurrealValue` variant (`SurrealRecordIdValue`, `StringSurrealValue`, …) passed as a typed binding through `tx.QueryAsync(sql, bindings, ct)`. No formatted literals for user values anywhere. `SurrealFormatter` was trimmed to just `Identifier()` validation (preview.42).

**Bonus footnote (also obsolete):** the old LET-prefix shape clipped large commits — SurrealDB had per-query statement-count / body-size limits that big aggregate writes blew past. With per-entity SaveAsync streaming each command as its own RPC inside the txn (preview.29), the multi-statement-batch concern doesn't apply.

**How to apply:** Don't reach for this memory's advice. If you're hitting a "no rows returned" mystery on a real DB today, the cause is something else — check `EnsureSuccess()` is called on the response (preview.44 fix), check the predicate compiles right, check you're targeting the correct DB/namespace. Treat this memo as why-we-built-SurrealFormatter context, not an action item.
