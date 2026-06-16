---
name: One catcher per dispatch method, many throwers — fail-closed at the session boundary
description: Each session-level async dispatch method (SaveAsync / DeleteAsync / RelateAsyncCore / UnrelateAsync) wraps its body in exactly one try { ... } catch { closed = true; throw; }. Everything else throws freely. The session has no other exception handlers, no swallow-and-continue, no log-and-ignore.
type: feedback
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
The session is the substrate boundary, and every async dispatch method is a single point where exceptions are handled. The shape:

```csharp
public async Task SaveAsync(IEntity entity, SurrealTransaction tx, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(tx);
    ThrowIfClosed();
    try
    {
        // entity walk + tx.CreateAsync / tx.UpsertAsync / children recurse
    }
    catch
    {
        closed = true;
        throw;
    }
}
```

Same shape on `DeleteAsync`, `RelateAsyncCore`, `UnrelateAsync`, and `FetchAsync`'s inner dispatch.

**Rules:**
1. **One catcher per dispatch method.** Each public async session method that talks to the substrate has exactly one try/catch wrapping the whole dispatch body. No nested catches, no per-statement guards.
2. **Many throwers.** All other code in the runtime throws freely. No defensive try/catch, no swallow-and-continue, no log-and-ignore. If something is wrong, throw. The dispatch catcher will handle it.
3. **Fail-closed.** Any exception during dispatch closes the session (`closed = true`) and propagates. The domain catches the rethrown exception and decides whether to reload-and-retry or give up. The session never recovers — there is no half-committed state to reason about (the user's `SurrealTransaction` is the unit of recovery; cancel it, open a new one, reload, retry).
4. **Boundary translators are sanctioned.** Code at a boundary that converts raw external errors into clean exception types is allowed to catch — it's implementing the boundary contract, not session-layer recovery. Examples in current code:
   - `HydrationValue.TryReadRecordId` / `TryReadReferenceId` — value-shape probing for the hydration boundary; returns null/false on shape mismatch instead of throwing.
   - `Disruptor.Surreal` SDK — translates raw transport / RPC framing errors into typed exceptions (`SurrealRpcException`, `SurrealConflictException`, etc.). Surface code consumes those typed exceptions; it doesn't translate again.

**Why:** This must live on the substrate boundary because that's where remote state changes and where partial failure is unrecoverable. Aborting a substrate-layer object (the session) on anything other than a substrate-layer issue is wrong-shaped — but any exception thrown through a dispatch method's body counts as "substrate-layer issue" because the session can't reason about whether the wire-level write landed. Mark closed, rethrow. The user's `SurrealTransaction` lifecycle (commit-or-cancel) carries the actual recovery decision.

**How to apply:**
- When tempted to add a try/catch anywhere except a session dispatch method or a documented boundary translator, default to NO. Just throw.
- Validation, invariant checks, identity-map poison detection, etc. all throw freely — they don't need to "handle" anything.
- If a boundary needs to translate raw I/O exceptions into a domain type, that's the only legitimate non-session catch — but it should be in a clearly-named boundary helper, not scattered through generic code.
- New dispatch methods follow the same try/catch shape: `try { … real work … } catch { closed = true; throw; }`. Don't be clever.

**History:** Originally formulated as "one catcher in CommitAsync" when there was a single `CommitAsync` that flushed the dirty batch. Rewritten preview.45-era as the per-dispatch-method version after CommitAsync went away with the streamed-commit pivot — the principle survives, the site multiplied.
