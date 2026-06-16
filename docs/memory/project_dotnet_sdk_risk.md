---
name: SurrealDb.Net is "second-cousin energy" — strategic risk to depend on
description: The official .NET SurrealDB SDK lags Rust/Go/TS/Node, has known issues (CBOR, JSON Thing-binding), and may be deprioritized. Already routing around it. Building a 1:1 wire client (Step 0 of the unpainting plan) is the de-risking move.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
The official .NET SDK (`SurrealDb.Net`) is visibly less invested-in than Rust/Go/TS/Node — second-cousin energy. The CBOR projection workaround in `Disruptor.Surface.Transport.Embedded` and the JSON Thing-binding gotcha (see `project_surreal_rpc_binding.md`) are the visible tips of "the people maintaining this SDK are not the people designing the SurrealDB protocol."

**Strategic risk:** Every architectural choice we make from here that depends on protocol features (cross-request txn-id propagation, native conflict surfacing, live queries, schema introspection, anything else v3 ships) is gated on the SDK exposing them on a timeline we don't control. We're already routing around it; pinning the wire layer to it bakes the risk in.

**Why this matters now:** the unpainting plan (`project_unpainting_plan.md`) needs control over wire-level details (txn-id capture, conflict-error mapping, payload threading). Working through SurrealDb.Net for those means waiting on someone else's roadmap.

**How to apply:** When the SDK can't do something we need, prefer building it ourselves over working around it. Step 0 of the unpainting plan (own a minimalist 1:1 client) addresses this directly. Embedded mode keeps delegating to the official Rust binding via the SDK because there's no point re-implementing RocksDB; HTTP and WebSocket transports are the candidates for replacement.

**Pitch framing:** "we built our own client because the official one wasn't enough" reads as investment; "we have an opinionated ORM that pins to a maintenance-status third-party SDK" reads as opportunism. The optics matter for adoption.
