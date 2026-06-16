---
name: The generator does not look at user methods
description: All model annotations are AttributeTargets.Property only. Methods on [Table] classes are plain user code; the generator emits nothing from them and never inspects them.
type: project
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
The generator does not interpret method names, signatures, or attributes. The
attribute is the entire contract, and every model attribute (`[Property]`,
`[Parent]`, `[Reference]`, `[Children]`, the `RelationAttribute` base, all
forward/inverse derivatives, the four reference-delete behaviors) is declared
`AttributeTargets.Property`. Roslyn rejects them on methods at the user's call
site, so the generator never even sees a method-bound annotation.

Concrete consequences:
- `MethodModel` / `MethodVerb` / `ParameterModel` were deleted (2026-04). The
  pipeline no longer iterates `table.Methods` — there is no `Methods` field on
  `TableModel` any more.
- Methods on `[Table]` classes are plain user code calling the `Session` DSL.
  If the user wants a domain verb, they write a one-line passthrough:
  `public void Restricts(IRestrictedBy x) => Session.Relate<Restricts>(this, x);`
- The only generator-emitted methods are the IEntity hooks (Bind, Initialize,
  Hydrate, Flush, OnDeleting), the property setter helpers (`__WriteField`,
  `__ClearField`), and the per-aggregate loaders (`Load{Root}Async`,
  `{Root}AggregateLoader.PopulateAsync`).

**Why:** Pinned by the user explicitly: "the session interface serves this
completely" — the library's contract ends at the Session DSL. Method-name
conventions or partial-method body emission would be the library imposing
opinion on the user's domain shape, which is out of scope. See
`feedback_session_is_the_contract.md`.

**How to apply:** When designing any new generator feature, never inspect
method declarations. Ever. If you find yourself wanting to emit something into
a user-declared method body, that's the wrong shape — extend the Session DSL
instead and let the user wire it up however they like.
