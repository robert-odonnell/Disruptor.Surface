---
name: user-interfaces-as-first-class-model-participants
description: "When a generator feature seems to need new attributes / new model nodes, first ask whether it can be expressed as 'a user-declared interface with a marker base in the runtime'. preview.54, preview.55, and preview.56 all fell out of this pattern and share machinery; a fourth axis (if one surfaces) probably will too."
metadata: 
  node_type: memory
  type: project
  originSessionId: 1fd960d5-e72a-4f6c-ba01-0efccf0ddb5b
---

The Disruptor.Surface generator's three variant-ergonomics features — union endpoints (preview.54), shared-shape relation interfaces (preview.55), and annotated shared-shape lift (preview.56) — turned out to be the same shape under different rotations:

- **preview.54.** User declares `partial interface IFooTarget : IRecordId`, attributes it `[Foo] : Out<RestrictsAttribute>`. Tables opt in via per-table marker partials. Generator emits hydration/save/schema branches.
- **preview.55.** User declares `partial interface ICodeSymbolEdge : IRelationVariant`. Variants opt in by adding it to their base list. Generator emits a static `Create<TKind>` factory.
- **preview.56.** User adds `[In]/[Out]/[Property]/[Id]` to the shared-shape interface members. Empty-body variants (`[Calls] partial class A : IFoo;`) inherit the shape via linker lift; the generator emits full property declarations on the variant. The interface becomes the source of model attributes for any variant that opts in by leaving its body empty.

All three reuse the same machinery: `RelationVariantExtractor` capturing `ImplementedInterfaceFullNames`, `RelationLinker` matching candidates to enrolled members, per-emitter "ship one partial fragment onto the user's type" template. The model is pull-don't-push throughout — every emitter consumes `ModelGraph`, none of them shadow it. preview.56 also showed the same pattern can extend the *shape* an interface contributes (not just markers / dispatch), with the linker doing the cross-pipeline join so incremental staleness stays correct.

**The principle:** when a generator feature seems to need new attributes / new model record types, first ask whether it can be expressed as **a user-declared interface deriving from a runtime marker (`IRecordId`, `IRelationVariant`, `IEntity`, ...) with members enrolling via base-list inclusion**. The generator's job becomes: discover the interface via base-chain walk, collect members, emit the API surface as a partial fragment on the user's own type. No new attribute. No new metaclass. The interface IS the model node.

**Why this works:**
- Partial class / partial interface declaration merging is the C# escape hatch for grafting generator output onto user-declared types without inheritance, base-class clashes, or marker-attribute proliferation.
- Treating user-declared interfaces as first-class participants (not just "annotations on classes") means the user expresses *what they want* in their own type vocabulary, and the generator emits the *how* alongside.
- The same `AllInterfaces` walk works for discovery on both sides — interface candidates ("which interfaces extend my marker?") and member candidates ("which types implement this interface?").

**When to apply.** Any future feature that smells like "let me give you a marker class that bundles related things together" or "let me dispatch over a set" or "let this contract be the source of N variants' shape" is a candidate. Try the user-declared-interface shape before adding attributes.

**Cross-pipeline gotcha (from preview.56).** When the generator wants to read state from interface A and apply it to variant B, do the join in the **linker**, not in the per-symbol extractor. Per-symbol extractors are cached by their own syntax; editing the interface won't re-run the variant's transform, so the variant model would be stale. The linker re-runs whenever any extractor's output changes, so cross-symbol joins live there.

**See also:** [[project_union_endpoints_design]], [[project_shared_shape_relation_interfaces]], and [[project_annotated_shared_shape_lift]] — the three concrete instances. Also [[feedback_minimal_surface]] — the corresponding user-side feedback ("use my library and I won't be intrusive") that this pattern serves.
