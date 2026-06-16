---
name: We were shadowing the SurrealDB storage engine — RESOLVED 2026-05-12
description: HISTORICAL framing that drove the unpainting plan. Named the unease that WriterLease/PendingState/CommitPlanner/snapshot-as-write were reimplementations of native MVCC / COW / optimistic concurrency / atomic batch. All four are gone (preview.29–.45). The in-session read snapshot survives as a deliberate post-load cache, not as substrate shadowing. Read for context on why the plan existed.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
The deep unease about the current design has a name: **the library re-implements primitives that SurrealDB's storage engine already provides natively, and does so at a higher abstraction layer where they fit less well.** Snapshot isolation, COW, optimistic concurrency control, atomic batch — every one of those is in the substrate. We've built parallel machinery on top that doesn't quite line up.

It's worse than redundant — it's *competing*. The DB has its own opinions about MVCC, lock granularity, conflict detection, isolation levels. WriterLease doesn't use any of those; it's a synthesized layer that prevents the DB's machinery from being used at all.

Honest accounting:
- **Real value we add**: domain modeling from attributes, single-query aggregate loads, identity map, reference-delete planning, typed relations, schema emit. None of these are in the DB.
- **Redundant with substrate**: snapshot isolation, change tracking, atomicity, read-your-writes, optimistic concurrency control. All in the DB's transaction model.

**Why:** Historically defensible — the .NET SDK couldn't span txns across requests, sync reads are useful, snapshot decoupled-from-connectivity makes offline reasoning easier. v3's cross-request-txn-by-id erodes the .NET-client-limitation reason, which was the strongest one.

**How to apply:** When evaluating any change that touches concurrency, atomicity, or read coherence, prefer leveraging the substrate over building parallel machinery. The current "load aggregate / mutate in memory / commit one script" model is one coherent corner of a multi-axis space — fine for small-to-medium aggregates with low write contention, but the symptoms (lease starvation, snapshot bloat, batch-size workarounds) are the corner's edges showing.

Pairs with `project_unpainting_plan.md` (the staged way out).

**Resolution (2026-05-12).** All four redundant primitives are gone: WriterLease (preview.29 → native `SurrealConflictException`), CommitPlanner (preview.34), PendingState (preview.35), and the relation write-buffer / snapshot-diff (preview.45). RelateAsync dispatches `INSERT RELATION IGNORE INTO {edge} $_content` against the deterministic Idempotent edge id (preview.49 — preview.46's UPSERT path was reversed because `TYPE RELATION ENFORCED` rejects UPSERT), so idempotence is owned by the substrate via primary key + `UNIQUE INDEX (in, out)`. Cascade re-anchored preview.47 — library predicts via `PlanDelete`, substrate enforces via `REFERENCE ON DELETE`. The read-side snapshot (`state.Entities` / `state.Edges` / `state.LoadedSlices`) survives **deliberately** as a post-load cache so aggregate dot-walks stay sync — that's a value-add, not substrate shadowing. Treat the framing as resolved; apply it as historical context, not as live tension.
