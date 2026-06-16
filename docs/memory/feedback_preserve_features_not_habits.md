---
name: Don't preserve existing behavior without the context
description: When evaluating constraints on a design, distinguish "designed property the library promises" from "habit the code happens to have." Default to questioning the habit; only treat constraints as load-bearing when the project's stated goals back them up.
type: feedback
originSessionId: 1e5d41a3-6a1f-4283-a81a-3bde6363f5b2
---
When evaluating an architectural change, watch for the failure mode: treating an *incidental* property of the current code as a *designed* constraint. The fix is to re-read the project's actual goal and check whether the "constraint" actually serves it.

**Concrete instance (2026-05-10):** working on Step 3 of the unpainting plan. I framed "sync property setters" as a hard constraint that foreclosed eager-flush. Spent considerable energy on options A–E to "preserve" sync setters. The user pushed back: the library's actual goal is "express semantic relationships naturally" — nothing in that goal speaks to sync vs async or eager vs lazy. The sync surface was a *habit* of the existing implementation, not a designed feature; the real architectural axis was "two distinct boundaries (Save / Commit)", not "eager vs lazy buffering." Once reframed, the answer (explicit Save) was clear.

**Why:** The user's words: "we're trying to preserve a non-feature. or better stated: preserve existing behavior without the context." Time wasted in the option-tree search came from treating a habit as a contract.

**How to apply:**
- When a design seems to have an awkward constraint, ask: is this constraint *named* anywhere in the project's stated goals / docs / contracts? Or is it just "how the code currently happens to work"?
- If it's the latter, treat it as a candidate for re-evaluation rather than as a fixed input.
- Be especially suspicious when "preserving X" leads to options that all feel like compromises. The fact that no option is satisfying is often a signal that the wrong thing is being preserved.
- Memory entries flagged as feedback (`feedback_async_is_methods.md`, etc.) describe the *current* surface — they're observations of habit, not contracts. Reasoning *from* them ("we promised sync, so we must preserve sync") is exactly the failure mode this memory warns against.
