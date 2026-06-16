---
name: Minimal library surface area
description: Architectural direction — "use my library and I won't be intrusive on your code"; prefer zero-line defaults and single-attribute escape hatches
type: feedback
originSessionId: 47798b2b-cae8-4f4f-8004-5865aed8c575
---
When designing any user-facing API in this library, keep the surface the user must write as small as possible. Prefer: sensible default → zero user code; override → one attribute; custom extension → one class with an attribute. Never require users to inherit from library base types, declare marker types, or hand-write boilerplate per entity.

**Why:** The user stated the explicit architectural direction — *"use my library and I won't be intrusive on your code"*. The typed-id design discussion crystallised this: three options were on the table (assembly attribute / marker partial struct / declared translator), and the user pushed me to collapse to the smallest-surface option and even default that away when possible.

**How to apply:** When sketching any new feature (id system, validation hooks, serialization, etc.), start from "what can the user write zero of?" and only add required surface when a default cannot reasonably be chosen. When multiple shapes compete, prefer the one that forces the fewest declarations/inheritances/registrations on user code. Discovery patterns (attributes scanned by the generator) are preferred over base-class inheritance or interface implementation on user types.
