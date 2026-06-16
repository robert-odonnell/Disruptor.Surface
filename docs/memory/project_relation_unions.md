---
name: Per-relation union interfaces (forward + inverse, entity + id)
description: Generator emits I{InverseName} marker interface per multi-target relation kind, I{Singularize(ForwardName)} per multi-source kind. Each entity union has a parallel id-side I{...}Id. Single-member sides skip emission.
type: project
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
For each forward relation kind, `RelationLinker.ComputeUnions` walks every table's relation members and builds a **source set** (tables with the forward attribute) and a **target set** (tables with the matching inverse attribute). When a side has 2+ members it becomes a `RelationUnion` with a marker interface; single-member sides skip emission and stay typed to the concrete entity.

**Naming follows the schema's own language**:
- **Target** interfaces use the inverse attribute name → `I{InverseName}` (e.g. `IRestrictedBy`, `IReferencedBy`, `IConcernedBy`, `IRevisedBy`, `IInformedBy`, `ICitedBy`, `IResolvedBy`, `IAssessedBy`). Past-participle naming reads naturally — "Design IS RestrictedBy".
- **Source** interfaces use the singularised forward attribute name → `I{Singularize(ForwardName)}` (e.g. `IRestrict`, `IReference`, …). 3rd-person verb forms get the trailing `s` stripped via `Humanizer.Singularize`.

The entity-side interfaces inherit `IEntity` and have empty bodies. `PartialEmitter` adds them to each member's base list automatically (alongside `IEntity`). **Each entity union also has a parallel id-side interface** named `I{InverseName}Id` (target) or `I{Singularize(ForwardName)}Id` (source). The id-side interfaces inherit `IRecordId`, and `IdEmitter` adds them to the base list of every `{Name}Id` struct in the union.

**Today's role:** with the typed `Session.Relate<TKind>(IRecordId, IRecordId)` / `Session.Relate<TKind>(IEntity, IEntity)` primitives and the directional `Session.QueryOutgoing<TKind, T>(this)` / `Session.QueryIncoming<TKind, T>(this)` reads, the union interfaces are not used as constraints on the runtime methods themselves (those just take `IRecordId` / `IEntity`). They appear instead as the parameter type in user-written domain-verb passthroughs:

```csharp
public void Restricts(IRestrictedBy x) => Session.Relate<Restricts>(this, x);
```

…and as the element type of relation-collection property reads (which the generator routes through `QueryOutgoing` on the forward side, `QueryIncoming` on the inverse side).

**Why:** Multi-target relations need compile-time type safety on the write call site (the user wants `constraint.Restricts(invalidThing)` to refuse to compile when `invalidThing` isn't `IRestrictedBy`). The interface is the schema's `TO designs|constraints|epics|...` reified in C# — one interface per union, every member implements it. The id-side family extends the idea to cross-aggregate relations where only the typed id is available.

**How to apply:** When adding new relation attributes or changing the schema's `FROM`/`TO` lists, the linker recomputes unions automatically. If you need to widen or narrow a union, add/remove `[InverseAttribute]` declarations on the relevant entity classes. Don't try to reference the generated union interface in user-side `partial` member declarations — only generator-emitted code (and user-written method bodies, which compile after emit) can use them.
