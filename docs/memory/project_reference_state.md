---
name: Cascade-on-delete — re-anchored preview.47
description: DeleteAsync runs a three-phase pre-flight resolve over state.Entities + IReferenceRegistry before dispatching. Library predicts what cascades (deterministic from the schema we emitted); substrate enforces via REFERENCE ON DELETE clauses. OnDeleting fires on every cascaded entity; Unset mirrors into in-memory backing fields via IEntity.SetReferenceTo; CascadeRejectException throws pre-dispatch on steady-state blockers. Step 5 of the unpainting plan, shipped 2026-05-12.
type: project
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
The old `CommitPlanner.ResolveReferenceDeletes` ran in-library cascade planning against `PendingState.References` immutable transitions. CommitPlanner / PendingState were deleted (preview.34/.35); cascade re-anchored preview.47 with `state.Entities` + `IEntity.EnumerateReferences()` as the data source instead.

**Current shape of `DeleteAsync(entity, tx, ct)`:**
1. `PlanDelete(entity.Id)` — three-phase resolve, may throw `CascadeRejectException` (no wire dispatch on throw).
2. `OnDeleting()` fires on every cascaded entity (directly-deleted + transitively cascaded).
3. `IEntity.SetReferenceTo(field, null)` for every Unset action — mirrors the substrate's `REFERENCE ON DELETE UNSET` into in-memory entity backing fields so subsequent reads off the snapshot don't return stale ids.
4. `tx.DeleteAsync(entity.Id.ToSdk(), ct)` — single typed DELETE; substrate cascades the rest under its `REFERENCE ON DELETE CASCADE` clauses.
5. `Record(Command.Delete(entity.Id))` — diagnostic log entry for the directly-deleted entity.
6. `CleanupLocalState` for every cascaded id — drops entries from `state.Entities` and any edges touching them from `state.Edges`.

**Three-phase resolve in `SurrealSession.PlanDelete`:**
1. **BFS classify.** Walk outward from the target. For each entity in `state.Entities`, enumerate its references via `IEntity.EnumerateReferences()`. For each (field, refTarget) pair where refTarget hits a doomed id, look up the policy from `IReferenceRegistry.IncomingReferencesTo(currentTable)` matched on (referencer table, field name). Classify: Cascade enqueues; Unset records pending null-write; Reject collects a *provisional* blocker; Ignore skipped.
2. **Filter rejecters whose owner is itself cascading.** A rejecter whose owner is in the cascade set is going away too — the reference goes with it; not a steady-state blocker. Only rejecters whose owner survives count.
3. **Throw on steady-state blockers.** If any survive, throw `CascadeRejectException` carrying every blocker. Same filter applied to Unset actions (no point nulling a field on an entity that's about to be cascaded).

**The multi-pass property that drove the three-phase design:** A points at C with Reject AND at D with Cascade; D points at C with Cascade. Delete C: D cascades → A cascades (via D's incoming Cascade) → A's provisional Reject is filtered (its owner is in the cascade set) → no false blocker. A single-pass collector (Reject as it walks) would have thrown a false rejection before cascading A. Test `DeleteAsync_MultiPassResolve_RejecterCascadesAwayBeforeBlocking` covers this.

**What the substrate covers via the schema:**
- `[Reject]` → `REFERENCE ON DELETE REJECT` on the field DDL — substrate raises an error at COMMIT if a deleted record is still referenced. Library's pre-flight Reject is the better-UX layer; substrate's REJECT is the safety net for un-modeled refs (off-contract schema mutation, etc.).
- `[Cascade]` → `REFERENCE ON DELETE CASCADE` — substrate cascades the delete; library predicts the same set for snapshot mirroring.
- `[Unset]` → `REFERENCE ON DELETE UNSET` — substrate sets the referencing field to NONE; library mirrors via `SetReferenceTo(field, null)` on the entity.
- `[Ignore]` → `REFERENCE ON DELETE IGNORE` — substrate leaves referencing rows alone (dangling refs allowed). Library does nothing too.
- `[Parent]` → always `REFERENCE ON DELETE REJECT` (you can't drop a parent that still has children).
- `SchemaEmitter.cs:407-411` is the single mapping site.

**Generator emission:** PartialEmitter emits `IEntity.EnumerateReferences()` for every entity with `[Reference]` or `[Parent]` (yields `(snake_case field name, _{name}Id backing field)` per property), plus `IEntity.SetReferenceTo(field, value)` for every entity with at least one nullable `[Reference]` (switch over field name → write both id backing field and clear cached entity-ref backing field). Non-nullable `[Reference]` and `[Parent]` are skipped from `SetReferenceTo` — schema emits REJECT for those, so they never enter the Unset phase.

**Risk:** Prediction depends on the schema in the DB matching what we emitted. Off-contract schema mutation (raw ALTER outside the library, foreign generator output) will diverge. This is bounded: the library is the canonical schema source for the tables it knows about, and `Workspace.ApplySchemaAsync` is the supported way to apply DDL. If a workflow mutates schema out-of-band, the contract is broken anyway.

**How to apply:** When adding a new `[Reference]` shape (e.g. a new policy enum value, a non-table reference target), update both `ReferenceDeleteBehavior` and the planner's policy switch in `PlanDelete`. When adding a new entity property kind that participates in the cascade (e.g. cross-aggregate edges), update `EnumerateReferences` emission in PartialEmitter. Tests in `SurrealSessionTests.cs` (`DeleteAsync_*`) cover the four canonical cases; new cascade shapes deserve a test pair.
