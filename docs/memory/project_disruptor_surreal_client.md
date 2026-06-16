---
name: Disruptor.Surreal — first-pass v1 client at ../surrealdb-dotnet
description: Step 0 of the unpainting plan has shipped a first pass. CBOR-over-WebSocket SDK at ../surrealdb-dotnet (Disruptor.Surreal). Solid foundation; two integration concerns flagged. WebSocket-only, embedded permanently out. Tests green (85).
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
Step 0 of `project_unpainting_plan.md` has a first pass. New SDK lives at sibling repo `D:\Disruptor\surrealdb-dotnet` (`Disruptor.Surreal`). CBOR over WebSocket, single package, modeled on the official Rust client.

**What's in:** WS RPC lifecycle (send/receive loops, id-correlation, bounded outbound channel, ping, epoch-coalesced auto re-auth on token expiry); full CBOR tag set (0/6/7/8/9/10/12/13/14/37) with nanosecond-precise datetime; Root/NS/DB/Record/Token auth; bindings flow through CBOR `params` (not SQL inlining); **txn-id propagation via `BeginTransactionAsync` → `Guid` carried in every `RpcRequest`**; typed exception hierarchy (Connection/Auth/Conflict/TransactionAborted/Constraint/Protocol/Rpc); v3 version pin (`>=3.0.0-alpha.1, <4.0.0`); `IRecordId` interop hook; closed `Value` sealed-record hierarchy.

**What's out (deliberately, decided 2026-05-10):**
- **Embedded mode** — explicit "no" after reflection. "We trust the database." Door not closed forever, but no support today and no design choices made to keep its option live. Surface's existing `Disruptor.Surface.Transport.Embedded` cannot be the bridge for this SDK's tests.
- **HTTP transport** — deferred. Might add later; CBOR-RPC over WebSocket is the v1 contract. Step 1 of the unpainting plan only needs WS, so this isn't a blocker.
- **JSON wire format** — out (lossy for Thing/datetime/decimal). CBOR only.
- **POCO mapping** — out. Surface IS the mapper.
- **Live queries / reconnect-with-session-replay / refresh-token rotation** — out for v1.

**Why it matters for the unpainting plan:**
- Native txn-id-in-payload is the mechanism Step 1 needs. Available now.
- `SurrealConflictException` is the Step 2 hook for replacing WriterLease with native conflict surfacing.
- CBOR tag 8 round-trips RecordIds losslessly — invalidates `project_surreal_rpc_binding` *for this client*. Surface's SurrealFormatter SQL-inlining workaround becomes obsolete once we adopt the transport.

**Test seam — RESOLVED in preview.5 (2026-05-10):** `IConnection` promoted to public; `Surreal(IConnection)` ctor public. The chosen shape is cleaner than the original ask:
- `IConnection.SendAsync(string method, Value? @params, Guid? txnId, CancellationToken)` — wire envelope only. Method names are stable protocol strings; `params` is a `Value`. No internal `Command` types leak through the public seam.
- Internal `Command` DSL stays internal; `ConnectionCommandExtensions.SendAsync(this IConnection, Command)` (internal) bridges Surreal.cs's existing call sites.
- Auth-loop guard now matches by method name (`"signin" / "authenticate" / "signup"`) — same behavior, public-shape-friendly.
- `FakeConnectionTests` shows the seam: a `RecordingConnection { Responder = (method, params, txn) => … }` is the entire test setup.

**For Surface's Step 1**, this is exactly the seam needed: a fake `IConnection` can assert the exact wire sequence `CommitAsync` emits ("begin" → N "query"s with the captured txn-id → "commit") with assertions on the SurrealQL + bindings inside each `params` payload. No real server needed for unit-level "did we emit the right wire shape" tests.

**Embedded test story** — embedded is explicitly out of scope for the SDK. Surface's existing test harness needs a different path: most likely subprocess `surreal start memory` for integration tests; unit tests use the `IConnection` fake seam. Not blocking; resolve when Surface starts integrating.

**Distribution:** NuGet package `Disruptor.Surreal --version 0.1.0-preview.5`. Surface integrates via PackageReference, not cross-repo ProjectReference.

**Cadence note (preview.2 → .5):** live queries, refresh tokens, server version compat check, `RetryPolicy.WithRetryAsync`, NS/DB/Record credentials + `SignupAsync`, and Set/Range/File/Geometry value variants all landed. Feature matrix is down to "live/kill" being the only Rust-SDK item still explicitly open beyond the deliberately-out-of-scope ones.

**Smaller notes:** error classification falls back on message-substring matching (acknowledged "coarse on day one"); each method = one RPC (no coalescing — fine for Step 1); `Surreal` and `Transaction` duplicate methods rather than share via interface (deliberate readability tradeoff).

**How to apply:**
- When implementing Step 1 (streamed commit) in Surface, target this SDK; use `Transaction.QueryAsync` exclusively (Surface generates the SurrealQL — `tx.SelectAsync` etc. resource methods are irrelevant for our path).
- Push the `IConnection` visibility change before integration begins.
- Resolve the embedded-test story before swapping out Surface's current transport.
- When Step 2 lands, classify on `SurrealConflictException` from the SDK rather than re-classifying server messages downstream.
