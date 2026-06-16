---
name: SurrealDB v3 BEGIN returns a txn id carried in request payloads
description: v3's cross-request txn semantics — BEGIN TRANSACTION returns an id; subsequent requests carry it in payload to participate in the same txn. Connection pinning is a non-issue; HTTP can host multi-request txns. Removes a key constraint on the unpainting plan.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
SurrealDB v3's `BEGIN TRANSACTION` returns a transaction id. Subsequent requests carry that id in their payload to participate in the same txn. The txn is **not socket-scoped** — it travels by id, not by connection.

**Why this matters:**
- Connection pinning is a non-issue. Pooled transports work fine; no need for "session/handle" abstractions on `ISurrealTransport`.
- HTTP REST can host a cross-request txn, the same as WebSocket or embedded. Transports stop diverging at the commit/txn layer.
- Removes the strongest historical reason for the snapshot/lease design (the .NET-client-cant-span-txns limitation), making `project_unpainting_plan.md` Step 1 directly actionable.
- "Concurrent requests on the same txn id" is moot in our usage: within one CommitAsync the loop is serial; across commits the lease/CAS already serializes per aggregate.

**Still TBC, verify before depending on:**
- txn id idle-timeout
- behavior if the client dies between BEGIN and COMMIT (auto-rollback after timeout vs. stuck txn)

**Source:** confirmed by user during 2026-05-10 architectural conversation; not yet verified empirically against a v3 instance. Confirm before committing wire-shape design to it.

**How to apply:** When designing transaction-aware code, don't assume cross-request txns require a persistent connection. The wire layer just needs to thread the txn id through request payloads.
