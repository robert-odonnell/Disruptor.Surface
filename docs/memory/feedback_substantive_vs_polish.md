---
name: Don't conflate substantive refactors with polish
description: Code bloat, architectural spread, implementation laziness, blindspots, and real bugs are the categories of substantive work. Polish is cosmetic/stylistic and comes later. Don't undersell structural fixes by labelling them polish.
type: feedback
originSessionId: 87dba6cb-35ad-4c5e-831a-de7ab030c811
---
When characterizing the work in this codebase, distinguish substantive
refactors from polish:

**Substantive (the real work):**
- **Code bloat** — paths the generator doesn't legitimately use, scaffolding
  for hypothetical demand, dead branches, redundant state.
- **Architectural spread** — concepts leaking across boundaries, ambient
  state, parallel pipelines for paths that no longer fire.
- **Implementation laziness** — early returns that hide bugs, "good enough"
  validation that lets bad input through, helpers that only handle the
  single-X case when N-X is the real shape.
- **Blindspots** — assumptions baked into the design that no test exercises,
  silent precedence inversions between emitters, contracts that look
  enforced but aren't.
- **Real bugs** — behaviour that demonstrably breaks under realistic input
  (e.g. multi-source relations losing their loader subselect, post-bind id
  mutation corrupting the identity map, lease theft going undetected).

**Polish (comes later):**
- Renames, doc-comment tweaks, formatting passes, trivial XmlDoc additions.
- Reordering members for readability when behaviour is unchanged.
- Reflowing prose in CLAUDE.md / docs without changing the contract being
  described.

**Why this matters:** This session has been substantive throughout —
ripping method support (-329 lines), removing `[RecordIdValue<T>]`,
splitting the id anchor, adding CG022 / CG024, fixing the multi-source
loader silent-bug, redesigning WriterLease around CAS-on-seq. Calling that
"polish" undersells the structural shifts and would mislead future-me
about what kinds of work pay off.

**How to apply:** When summarizing recent work, name the category
accurately. If a commit removed silent-failure paths, that's a real bug
fix, not polish. If a commit collapsed three concerns into one (TTL +
holder + race detection → seq match), that's architectural simplification,
not polish. Reserve "polish" for genuinely cosmetic passes that wait until
after the structure is right.
