---
name: project-pending-cg036-lift-conflict-diagnostic
description: "Outstanding follow-up after preview.57 — CG036-style diagnostic for shared-shape lift conflicts. The merge currently fails closed (silent drop) on incompatible contributions, which is a confusing user-facing rough edge."
metadata: 
  node_type: memory
  type: project
  originSessionId: 8fa75f37-a55e-473f-8474-1fa398508461
---

After preview.57's merge refinement to [[project_annotated_shared_shape_lift]], the linker drops a variant silently when two annotated shared-shape interfaces (or the variant + an interface) contribute incompatible values for the same role/property — same fail-soft contract as malformed input, but no diagnostic.

This is a known rough edge. The natural follow-up is a `CG036` (next free diagnostic id after CG035) named "shared-shape lift conflict" — error severity, fired from `ModelGenerator.Emit` after `RelationLinker.LiftVariantsFromSharedShape` produces its filtered variant list. Message format should name:
- The variant FQN that was dropped.
- The two contributors that disagree (variant own / interface A / interface B).
- The role + property name where they disagree (e.g. `Source / [In] CodeSymbolId` vs `Source / [In] OtherSymbolId`).
- Hint at how to resolve (split the variant, fix the type, drop one base).

Implementation sketch:
- `TryMergeLift` / `TryMergeSingular` / `CompatibleProperty` in `RelationLinker.cs` currently return `bool`. Either thread an `out` conflict descriptor up to `Build`, or have the linker collect a parallel `EquatableArray<string>` of conflict descriptors onto `ModelGraph` (mirror of `AggregateConflicts` / `CascadeCycles`) that `ModelGenerator.Emit` walks to report `CG036`.
- Add a `Lift_ConflictingAnnotatedInterfaces_FiresCG036` test alongside the existing drop test.

**Why this isn't urgent:** the failure mode is rare (variants would have to inherit from two annotated interfaces that disagree on shape, which is unusual in practice) and the current silent drop matches the existing fail-soft contract for malformed inputs. **Why it's still worth doing:** when it does hit, the symptom is "variant didn't generate" — a confusing missing-emit, hard to root-cause without reading the linker source.

User flagged this on 2026-05-13 just after preview.57 landed.
