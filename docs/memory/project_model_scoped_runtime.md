---
name: Model-scoped runtime metadata on the [CompositionRoot] partial
description: No process-global state. Schema and ReferenceRegistry are static members on the user's [CompositionRoot] partial. SurrealSession takes the registry in its ctor; CommitPlanner reads from it. Multiple Surface-generated assemblies coexist in one process.
type: project
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
Surface used to have process-global state: a `Surface.Runtime.ReferenceRegistry` static facade with a `[ModuleInitializer]` registering the consumer's `GeneratedReferenceRegistry`, and a `Surface.Runtime.GeneratedSchema` static class. Both broke if you tried to load two Surface-generated assemblies in the same process: last-writer-wins on the registry, type collisions on the schema.

**Fix (2026-04, punch list #10):** model-scoped via the user's `[CompositionRoot]` partial.

**Runtime:**
- `IReferenceRegistry` interface stays as the contract.
- `NullReferenceRegistry.Instance` for sessions that don't need reference-delete planning (default for parameterless ctor, used in tests).
- `SurrealSession(IReferenceRegistry registry)` ctor; parameterless overload defaults to `NullReferenceRegistry`. `ReferenceRegistry` exposed as a public property.
- `CommitPlanner.Build(pending, registry)` takes the registry; `ResolveReferenceDeletes` + `EffectiveIncomingReferences` read through it.

**Generator (per-consumer, not per-runtime):**
- `ReferenceRegistryEmitter` emits an internal sealed `GeneratedReferenceRegistry` impl in the user's [CompositionRoot] namespace AND a partial fragment of the [CompositionRoot] adding `public static IReferenceRegistry ReferenceRegistry`.
- `SchemaEmitter` emits an internal `SurfaceSchema` companion with the chunk array AND a partial fragment of the [CompositionRoot] adding `public static IReadOnlyList<string> Schema` plus `public static Task ApplySchemaAsync(transport, ct)`.
- `CompositionRootEmitter` passes `ReferenceRegistry` into the new `SurrealSession` ctor inside the emitted `Load*Async`.
- All four skip emission when no [CompositionRoot] is declared.

**Caller surface:**
```csharp
await Workspace.ApplySchemaAsync(transport);          // boot
var workspace = new Workspace();                       // user-owned ctor
var session = await workspace.LoadDesignAsync(transport, designId);   // session has registry
await session.CommitAsync(transport, lease);           // planner reads model registry
```

**Why:** "It is not a good final architecture if you ever load multiple generated models, test assemblies, plugins, or tools in the same process." The [CompositionRoot] is already where the user's domain anchor lives — bolting Schema + ReferenceRegistry as static members onto the same partial keeps the surface area to one user-named anchor, no new naming conventions, no global state.

**How to apply:** Anything new the runtime needs at commit time gets passed into `SurrealSession`'s ctor (or grafted onto the [CompositionRoot] as a static partial-fragment member). Never reach into a process-global facade. When emitting new model-scoped metadata, use the same pattern: internal companion class for the data + partial fragment on [CompositionRoot] for the public accessor.
