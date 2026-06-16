---
name: User code can't reference generator-emitted types in the same analysis pass
description: Source-generator gotcha — Roslyn analyzes user source before the generator emits, so user-side declarations that reference generated types fail with CS9255 / CS0246. Generator-emitted code can reference other generator-emitted types fine.
type: project
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
When the user writes `partial IReadOnlyCollection<IRestrictedBy> Foo { get; }` and `IRestrictedBy` is a generator-emitted interface, Roslyn tries to resolve `IRestrictedBy` *during the same analysis pass* in which the generator would emit it. The interface doesn't exist yet from Roslyn's POV → it captures an error type. The generator's `TypeRefBuilder` records the unresolved name; the emitted partial impl gets a `FullyQualifiedName` that doesn't match the user's declaration; the build fails with `CS9255 (partial member declarations must have the same type)` plus `CS0246 (type not found)` in the generated `.g.cs`.

**Generator-emitted code referencing other generator-emitted code is fine** because both files compile together in the later pass. So the workaround is: keep the user-side signature pointed at types that already exist (typically `IEntity`), and only use the generator-emitted type inside generator-emitted code (e.g. as the parameter type of a default protected mutator, where the type literal is written into the `.g.cs` file directly).

This is why:
- User declares `[Restricts] partial IReadOnlyCollection<IEntity> Restrictions { get; }` (not `IReadOnlyCollection<IRestrictedBy>`).
- Generator emits the partial body referencing `IRestrictedBy` and the typed marker class `Restricts` directly — those are written into the `.g.cs` and compile together.
- User-written *bodies* (not partial-member declared types) can reference emitted types fine because expression-position resolution happens after generator emit. So `public void Restricts(IRestrictedBy x) => Session.Relate<Restricts>(this, x);` works — `Restricts` (the marker class) and `IRestrictedBy` (the union interface) are resolved at body-compile time.

**Why:** Hit this directly when wiring the union interfaces — the obvious "let users type their relation collections to the union" approach fails because of Roslyn's pipeline order. Cost a build cycle to discover; worth pinning the workaround.

**How to apply:** Anytime the generator emits a new type that user code might want to reference, ask first whether the reference can live in generator-emitted code instead. If user-side reference is unavoidable, the type has to be either pre-defined in the runtime project (so it exists before any generator pass) or supplied in `RegisterPostInitializationOutput` (which runs before user-source analysis). Types computed from user-source analysis cannot be referenced by user source in the same compilation.
