---
name: project-annotated-shared-shape-lift
description: "preview.56 lifted [In]/[Out]/[Property]/[Id] from shared-shape interfaces onto empty-body variants — extends preview.55 from \"factory only\" to \"shape source\""
metadata: 
  node_type: memory
  type: project
  originSessionId: 8fa75f37-a55e-473f-8474-1fa398508461
---

preview.56 (DONE 2026-05-12) extended the shared-shape relation interface introduced in preview.55 from "kind-keyed Create<TKind> factory only" to also serving as the source of model attributes (`[In]/[Out]/[Property]/[Id]`).

Shape:
- Annotate the interface members: `public partial interface IFoo : IRelationVariant { [In] T Source { get; set; } [Out] T Target { get; set; } [Property] string Payload { get; set; } }`.
- Variant body collapses to nothing: `[Calls] public partial class CallsRelation : IFoo;`.
- Generator emits full (non-partial) auto-property declarations on the variant that satisfy the interface contract via partial-class declaration merging, plus the usual IEntity scaffolding (Hydrate, SaveAsync, EnumerateReferences) and per-kind sidecars.

**Why:** the per-variant `[In]/[Out]/[Property] partial T Source/Target/Payload { get; set; }` boilerplate was the last hand-maintained per-variant declaration after preview.55 collapsed the construction switch. Lifting these onto the interface makes a single contract declaration the source of truth for shape across N variants. Falls cleanly out of [[project_user_interfaces_as_first_class_model_participants]] — same pattern as preview.54 / .55 (user declares partial interface deriving from a runtime marker; generator emits onto the user's type).

**How to apply:**
- Lift is *opt-in* — annotate the interface to use it. preview.55 self-describing variants (own attributed `partial` members) still work and continue to win wherever they overlap with an interface contribution.
- The linker MERGES rather than picks: it walks every annotated shared-shape interface the variant implements (transitive base closure), accumulating non-overlapping contributions. Local self-declared members and compatible interface fragments merge cleanly; only HARD conflicts (overlapping role+name with incompatible Type / IsNullable) drop the variant and report CG036.
- The merge composes across the interface chain too: payload shape can live on `IPayload : IRelationVariant`, endpoint shape on `IEdge : IPayload`, and a variant `: IEdge` gets both — `SharedShapeExtractor` walks `iface.AllInterfaces` + `iface` itself, sorted by FQN for stability.
- Half-populated variants (declares own `[In]` but not `[Out]`, or vice versa) now pass through the extractor with one endpoint null for the linker to fill — only multiplicity violations (`>1` of a role) still fail at extraction.
- Unannotated shared-shape interfaces (preview.55 default) → lift is inert; an empty-body variant under one produces no emit.
- Implementation: `SharedShapeInterfaceCandidate` carries `LiftedIn/Out/Id/Payload`; `RelationVariantModel.In/Out` are nullable; `RelationLinker.LiftVariantsFromSharedShape` runs the merge between extractor and emit via `TryMergeLift` / `TryMergeSingular` / `CompatibleProperty` (role+name+type FQN+IsNullable must match for overlapping pieces); `RelationVariantEmitter` emits `partial T Name` only when `IsPartial=true` on the property (interface members carry `IsPartial=false`).

Tests: 283/283 green; sample: `src/Disruptor.Surface.Sample/Spike/InheritsRelation.cs` exercises the empty-body shape end-to-end with a new `[Inherits]` kind.
