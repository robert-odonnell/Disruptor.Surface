---
name: project-cg036-lift-conflict-diagnostic
description: "DONE 2026-06-17 — CG036 reports shared-shape lift conflicts instead of leaving users with a missing-emit symptom."
metadata: 
  node_type: memory
  type: project
  originSessionId: 8fa75f37-a55e-473f-8474-1fa398508461
---

After preview.57's merge refinement to [[project_annotated_shared_shape_lift]], the linker originally dropped a variant silently when two annotated shared-shape interfaces (or the variant + an interface) contributed incompatible values for the same role/property.

This is now resolved by `CG036` ("shared-shape lift conflict") — error severity, fired from `ModelGenerator.Emit` from `ModelGraph.SharedShapeLiftConflicts`. The message names:
- The variant FQN that was dropped.
- The lifted shared-shape interface.
- The lifted and existing role + property shapes where they disagree (e.g. `Source / [In] CodeSymbolId` vs `Source / [In] OtherSymbolId`).

Implementation:
- `RelationLinker.TryMergeLift` threads an `out SharedShapeLiftConflict?` up to `Build`.
- `ModelGraph.SharedShapeLiftConflicts` carries the descriptors.
- `ModelGenerator.Emit` walks those descriptors and reports `Diagnostics.SharedShapeLiftConflict`.
- `Lift_ConflictingAnnotatedInterfaces_FiresCG036_AndDropsVariant` pins the behavior.

User flagged this on 2026-05-13 just after preview.57 landed; it was implemented on 2026-06-17.
