---
name: Session DSL is the library's contract; method shape is user concern
description: The runtime hands tracked entities a Session — that DSL is the entire surface the library guarantees. The library does not impose method shape, naming, verbs, or domain expression patterns on the user.
type: feedback
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
The library's job is to track entities and serve the domain model. The `Session`
parameter handed to each entity is now a tidy DSL (Track / SetField / Delete /
Relate&lt;TKind&gt; / Get / GetReference / QueryChildren / QueryOutgoing / etc.)
and that DSL is THE contract. What the user does with the Session inside their
domain methods is none of the library's business.

**Why:** the user has explicitly stated this design principle: "we serve the
domain model, so it's not our business what the domain does with an instance
(albeit tracked by us)... the session interface serves this completely." The
implication is a hard scope boundary: everything the library guarantees to the
user lives on the Session DSL. Things like Add/Remove/Clear naming conventions,
domain-verb method synthesis, partial-method body emission, MethodVerb parsing
— these are all opinion the library has no business imposing.

**How to apply:**
- When tempted to add a generator path that emits user-domain methods (default
  mutators, partial-method body fills, verb-based dispatch), default to NO.
  The user can write a one-line passthrough using the Session DSL if they want
  a domain verb.
- New surface area should land on `SurrealSession` (or close adjuncts like
  `IHydrationSink`), not on the generator's emitted code.
- When evaluating "should the generator do X for the user?": if X can be
  expressed as plain user code calling the Session DSL, the answer is no.
- Source-generator emit is for tracking-plumbing scaffolding (Bind / Initialize
  / Hydrate / Flush, id structs, union markers, schema chunks, aggregate
  loaders, reference registry) — not for shaping the user's domain expression.
