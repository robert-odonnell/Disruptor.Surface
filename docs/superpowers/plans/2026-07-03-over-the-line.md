# Over the Line — preview.60 Release Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two uncaptured findings from `docs/review-main-2026-07-02.md` (relation-variant identity hole, missing `GetAll<T>()`), clean the stale docs, run the 7-item live-substrate validation pass from `docs/remaining-work.md` §1 plus the identifier-quoting probe, and cut the `v0.1.0-preview.60` release tag.

**Architecture:** Three code fixes (generator diagnostic + emitter hardening; one new runtime read method; comment/doc corrections), then a live-SurrealDB smoke extension to the existing sample harness, then release mechanics. No pipeline-model changes (no new `Model/` records), so incremental-caching risk is near zero — but the cache regression tests are the guardrail regardless.

**Tech Stack:** .NET 10 / C# (Roslyn incremental source generator on netstandard2.0 + net10.0 runtime), xUnit, SurrealDB 3.1.4 (local CLI, in-memory backend), GitHub Actions release on `v*` tags → NuGet.

**Explicitly out of scope (the line is the release tag, not the feature backlog):** delete-by-query, `[Assert]`/PERMISSIONS, migration diff, `[Table]` inheritance, GROUP BY/FETCH/streaming, live queries, ecosystem packages, AOT — all stay in `Improvements.md`. Identifier-quoting *implementation* is a gated decision (see Task 6 and Owner Decisions); only the *probe* is in scope.

## Global Constraints

- Test command: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj` — baseline is **475/475 green, 0 skipped** (verified 2026-07-03 on this machine).
- The `trackIncrementalGeneratorSteps` cache regression tests must stay green; any diagnostic added to `ModelValidation` must be a position-independent `PendingDiagnostic` (locations resolve through the declaration-location map at report time — never store `Location`/`ISymbol` in a model).
- Every `Model/` collection is `EquatableArray<T>` (no `Model/` changes are planned in this plan; if one becomes necessary, this rule is absolute).
- Do not write to `CLAUDE.md`; project documentation goes in `docs/notes.md` (maintain the engineering log — one entry per substantive tranche, newest first, `### unreleased — … (DONE 2026-07-03)` style).
- Diagnostic ids: CG001–CG056 are taken; **CG057 is the next free id** (verified by grep). Category constant is `"Disruptor.Surface"` (`Pipeline/Diagnostics.cs:7`).
- Branch: create `claude/over-the-line` off `master`; commit per task; PR at the end. (Execution should start via superpowers:using-git-worktrees.)
- Release mechanics: `release.yml` triggers on tags matching `v*` and packs whatever version `Directory.Build.props:13` declares (currently `0.1.0-preview.59`). A tag without the props bump would push a duplicate preview.59 to NuGet (silently skipped). Bare `preview.NN` tags do **not** trigger a build.
- Live substrate: `surreal` 3.1.4 is installed locally. Ephemeral server for the smoke pass (memory backend = fresh DB, which also answers the pre-existing-data concern for dev):
  `surreal start --bind 127.0.0.1:8000 --default-namespace project-brain --default-database workspace --username root --password secret`
  The sample's connection string is hard-coded to match (`src/Disruptor.Surface.Sample/Program.cs:12-13`).

---

### Task 1: CG057 — reject `[Id]` on relation variants

Relation-variant identity must be canonical (`RecordId.ForEdge(in, edge, out)`). A user-assignable `[Id]` lets `MarkSaved` record an id that differs from the row the `UNIQUE(in, out)` duplicate path actually updated (review finding 1, part a). Reject it with an **error** diagnostic covering both self-declared and shared-shape-lifted `[Id]` members. Extraction keeps classifying `[Id]` (the extractor tests and CG046 duplicate-role detection depend on it); rejection lives in `ModelValidation`. Emission is left unchanged — the emitted `[Id]` delegate is dead code behind a build error, and keeping it avoids a CS9248 wall next to the CG057.

**Files:**
- Modify: `src/Disruptor.Surface.Generator/Pipeline/Diagnostics.cs` (append after CG056, ~line 384)
- Modify: `src/Disruptor.Surface.Generator/Pipeline/ModelValidation.cs` (per-variant loop, lines ~145–215, where CG029/CG030/CG031 are computed; `Add` helper is at lines 934–946)
- Modify: `tests/Disruptor.Surface.Tests/Generator/DiagnosticsTests.cs` (two new positive tests, one negative)
- Modify: `tests/Disruptor.Surface.Tests/Generator/VariantSaveTests.cs` (fixture at line 47 declares the now-illegal `[Id]`; tests at 90–117 and 175–189 pin the removed behavior)
- Modify: `docs/api.md` (line 905 lift list; new CG057 row after line 1292 in the Diagnostics table)
- Modify: `docs/architecture.md` (line 61 pipeline-table row and line 142 — verify wording still holds; CG046 text stays, add CG057 where the lift list names `[Id]`)

**Interfaces:**
- Consumes: `ModelValidation.Add(pending, DiagnosticDescriptor, string[] args, string? typeKey, string? memberName, LocationInfo? exactLocation)`; post-link `RelationVariantModel.Id` (already merged with any shared-shape-lifted `[Id]` by `RelationLinker.LiftVariantsFromSharedShape`, `RelationLinker.cs:733–737`); duplicate-`[Id]` variants are already filtered into `RelationVariantIssues` (CG046) *before* this loop, so CG057 cannot double-fire on them.
- Produces: `Diagnostics.IdOnRelationVariant` (CG057, Error) — Tasks 2 and 7 reference its existence; no other task consumes it programmatically.

- [ ] **Step 1: Write the failing tests** in `DiagnosticsTests.cs`, mirroring the structure of `CG046_VariantWithDuplicateIdRole_IsRejected` (lines 748–775 — same harness call, same assert idiom):

```csharp
[Fact]
public void CG057_IdOnRelationVariant_IsRejected()
{
    const string src = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        namespace M;

        public sealed class RestrictsAttribute : ForwardRelation;

        [Table] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
        }

        [Restricts]
        public partial class WithId {
            [Id]  public partial RestrictsId Id { get; set; }
            [In]  public partial Constraint Source { get; set; }
            [Out] public partial Constraint Target { get; set; }
        }
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src);
    var diag = runDiags.FirstOrDefault(d => d.Id == "CG057");
    Assert.NotNull(diag);
    Assert.Equal(DiagnosticSeverity.Error, diag!.Severity);
    Assert.Contains("M.WithId", diag.GetMessage());
    Assert.Contains("Id", diag.GetMessage());
    // Member-precise location via the declaration-location map (memberName path).
    AssertLocationAt(diag, src, "RestrictsId Id");
}

[Fact]
public void CG057_LiftedIdFromAnnotatedSharedShape_IsRejected()
{
    // An [Id] can also arrive via the annotated shared-shape lift (preview.56/.57) —
    // the linker merges it into variant.Id, so a validation check on the post-link
    // model catches it with no extra linker work. The variant has no declared member
    // named 'Id', so the location degrades to the variant declaration — assert the
    // diagnostic fires; only assert location if the map's fallback lands somewhere
    // deterministic (verify DeclarationLocationExtractor's miss behavior in Step 3).
    const string src = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        namespace M;

        public sealed class RestrictsAttribute : ForwardRelation;

        [Table] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
        }

        public partial interface IEdgeShape : IRelationVariant {
            [Id]  RestrictsId Id { get; set; }
            [In]  Constraint Source { get; set; }
            [Out] Constraint Target { get; set; }
        }

        [Restricts]
        public partial class Lifted : IEdgeShape;
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src);
    var diag = runDiags.FirstOrDefault(d => d.Id == "CG057");
    Assert.NotNull(diag);
    Assert.Contains("M.Lifted", diag!.GetMessage());
}

[Fact]
public void CG057_DoesNotFire_ForTableIdOrIdlessVariant()
{
    const string src = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        namespace M;

        public sealed class RestrictsAttribute : ForwardRelation;

        [Table] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
        }

        [Restricts]
        public partial class Clean {
            [In]  public partial Constraint Source { get; set; }
            [Out] public partial Constraint Target { get; set; }
        }
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src);
    Assert.DoesNotContain(runDiags, d => d.Id == "CG057");
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj --filter "FullyQualifiedName~CG057"`
Expected: the two positive tests FAIL (no CG057 reported); the negative passes vacuously.

- [ ] **Step 3: Implement.** In `Diagnostics.cs`, append after `VariantPayloadTypeNotMappable` (CG056), matching its exact declaration style:

```csharp
public static readonly DiagnosticDescriptor IdOnRelationVariant = new(
    id: "CG057",
    title: "[Id] is not allowed on relation variants",
    messageFormat: "Relation variant '{0}' declares an [Id] member '{1}' (self-declared or lifted from a shared-shape interface). Relation-variant identity is canonical — the edge row id is always derived from (in, edge, out) via RecordId.ForEdge, and a user-assignable id desynchronises the session's identity map from the row the UNIQUE(in, out) duplicate path actually updates. Remove the [Id] member; the edge id is readable as ((IEntity)variant).Id once both endpoints are set.",
    category: Category,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);
```

In `ModelValidation.cs`, inside the existing per-variant loop (alongside CG029–CG031):

```csharp
// CG057 — relation-variant identity is canonical (derived via RecordId.ForEdge);
// a user-assignable [Id] can desynchronise MarkSaved from the UNIQUE(in, out)
// duplicate-update row. variant.Id here is post-link, so it covers both
// self-declared members and shared-shape-lifted [Id] contributions. Duplicate-[Id]
// variants never reach this loop (CG046 filtered them into RelationVariantIssues).
if (variant.Id is { } variantId)
{
    Add(pending, Diagnostics.IdOnRelationVariant,
        args: [variant.FullName, variantId.Name],
        typeKey: variant.FullName,
        memberName: variantId.Name);
}
```

While here, check `DeclarationLocationExtractor`'s behavior when `memberName` has no matching declared member (the lifted case): if a missed lookup falls back to the type declaration, nothing more to do; if it yields no location at all, pass `memberName: null` for the lifted case is not distinguishable on the model — accept `Location.None` degradation and note it in the test comment.

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj --filter "FullyQualifiedName~CG057"`
Expected: 3/3 PASS.

- [ ] **Step 5: Rewrite the now-broken `VariantSaveTests` fixture and tests.** CG057 makes `CrossAggregateModel` (line 47 `[Id] public partial TouchesId Id { get; set; }`) an error build, so in the same task:
  - Remove line 47 from `CrossAggregateModel` and update the fixture's `<summary>` (lines 26–31) — drop "Carries the optional user-facing [Id] (must win over the derive)".
  - Delete `E2E_UserAssignedId_WinsOverDerivation` (lines 175–189) — it pins the removed behavior.
  - In `Emits_MintId_DeterministicDerive_AndUserIdDelegate` (lines 90–117): rename to `Emits_MintId_DeterministicDerive`, delete the three `[Id]`-delegate assertions (lines 114–116) and their comment (111–113). Leave lines 102–109 untouched for now (Task 2 changes line 109).
  - In the `SaveCrossLinkAsync` helper (bottom of file): remove the `assignedIdUlid` parameter and its property-setting branch (only the deleted test used it).
  - `RelationVariantExtractorTests.cs:39–90` needs **no change** — it tests extraction, which still classifies `[Id]`.

- [ ] **Step 6: Full test run**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj`
Expected: PASS, count = 475 − 1 deleted + 3 new = **477** (adjust if Step 5 reveals another `[Id]`-pinning assert; the repo-wide fixture inventory says there isn't one).

- [ ] **Step 7: Docs.**
  - `docs/api.md:905`: the lift list "`[In]` / `[Out]` / `[Property]` / `[Id]` (preview.56+)" → drop `[Id]`, add "(an `[Id]` member on a shared-shape interface is rejected with CG057)".
  - `docs/api.md` Diagnostics table (after line 1292): add `| \`CG057\` | Error — \`[Id]\` on a relation variant (self-declared or lifted); edge identity is canonically derived from (in, edge, out). |`
  - `docs/architecture.md:61` and `:142`: update the two `[Id]`-on-variant mentions to note CG057 (CG046 duplicate-role wording stays).
  - `docs/notes.md`: add an `### unreleased — …` engineering-log entry (combine with Task 2's entry if executed together).

- [ ] **Step 8: Commit**

```bash
git add src/Disruptor.Surface.Generator/Pipeline/Diagnostics.cs src/Disruptor.Surface.Generator/Pipeline/ModelValidation.cs tests/Disruptor.Surface.Tests/Generator/DiagnosticsTests.cs tests/Disruptor.Surface.Tests/Generator/VariantSaveTests.cs docs/api.md docs/architecture.md docs/notes.md
git commit -m "feat(generator): CG057 rejects [Id] on relation variants — edge identity is canonical

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: `__MintId()` throws instead of minting a random id

Review finding 1, part b: reading a variant's id before both endpoints resolve currently mints a random `{Kind}Id.New()` that survives if endpoints are set later — recreating the duplicate-path drift. Replace the fallback with a throw. This also makes the anchor the *first* failure point for unset endpoints (the SaveContext reads `IEntity.Id` for its visited-set before the emitted SaveAsync body runs), so the save path's per-endpoint `InvalidOperationException("Endpoint '…' is not set.")` becomes defensive rather than primary — sweep any test that pinned those messages.

**Files:**
- Modify: `src/Disruptor.Surface.Generator/Emit/RelationVariantEmitter.cs` (`EmitIdAnchor`, lines ~356–400; doc comment lines 344–354)
- Modify: `tests/Disruptor.Surface.Tests/Generator/VariantSaveTests.cs` (assertion swap + one new test)
- Sweep: any test pinning the save-path "Endpoint '…' is not set." message on unset-endpoint saves (grep `is not set` under `tests/`)
- Modify: `docs/api.md:789` (the "**Edge ids are deterministic.**" paragraph documents both removed behaviors)

**Interfaces:**
- Consumes: `EndpointTryResolveExpression(...)` (RelationVariantEmitter.cs:411–430, unchanged); `variant.In!.Name` / `variant.Out!.Name` / `variant.Name` (available in `EmitIdAnchor`'s enclosing scope).
- Produces: emitted `__MintId()` whose unresolvable-endpoints branch is `throw new global::System.InvalidOperationException(...)` with message prefix `Cannot derive the edge id for '{VariantName}'` — Task 5's live smoke and any user docs rely on endpoints-before-Id-read semantics.

- [ ] **Step 1: Write/adjust the failing assertions.** In `Emits_MintId_DeterministicDerive` replace line 109:

```csharp
// OLD (delete):
Assert.Contains(": global::M.TouchesId.New();", src);
// NEW:
Assert.Contains(": throw new global::System.InvalidOperationException(", src);
Assert.Contains("Cannot derive the edge id for 'CrossLink'", src);
Assert.DoesNotContain("TouchesId.New()", src);   // src is the variant's .g.cs only — safe scope
```

Update the comment at lines 105–107 ("falls back to the random mint" → "throws — endpoints must be set before Id is read").

Add a new runtime-behavior test (reuse the file's existing instantiation idiom from `SaveCrossLinkAsync` — reflection over `GeneratorHarness.CompileAndLoad(CrossAggregateModel)`):

```csharp
[Fact]
public void ReadingVariantId_BeforeEndpointsAreSet_Throws()
{
    var asm = GeneratorHarness.CompileAndLoad(CrossAggregateModel);
    var variant = (IEntity)asm.CreateInstance("M.CrossLink")!;   // ← match SaveCrossLinkAsync's construction exactly

    var ex = Assert.Throws<InvalidOperationException>(() => variant.Id);
    Assert.Contains("Cannot derive the edge id for 'CrossLink'", ex.Message);
    Assert.Contains("Source", ex.Message);
    Assert.Contains("Target", ex.Message);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj --filter "FullyQualifiedName~VariantSaveTests"`
Expected: FAIL — emitted source still contains `: global::M.TouchesId.New();`.

- [ ] **Step 3: Implement.** In `EmitIdAnchor`, replace the fallback line

```csharp
writer.Line($": {idTypeFqn}.New();");
```

with

```csharp
writer.Line($": throw new global::System.InvalidOperationException(\"Cannot derive the edge id for '{variant.Name}': endpoints '{variant.In!.Name}' and '{variant.Out!.Name}' must both be set before Id is read — edge ids are canonical, derived from (in, edge, out).\");");
```

Rewrite the method's doc comment (lines 348–354): the precedence prose becomes "a hydrated id wins (`??=` only mints when `_id` is null — Hydrate writes `_id` directly); endpoints that are unset at first read throw instead of deriving, so no random id can ever be recorded by MarkSaved." Keep the lines-344–346 rationale (mint lives in the anchor because SaveContext reads `IEntity.Id` first) — still true and it now explains why the throw surfaces there.

- [ ] **Step 4: Run VariantSaveTests, then sweep**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj`
Expected: VariantSaveTests PASS. If any other test now fails because an unset-endpoint save throws the anchor message before the save-path per-endpoint message, update that test's expected message (same exception type). Grep to find candidates: `grep -rn "is not set" tests/`.

- [ ] **Step 5: Rewrite `docs/api.md:789`.** The "**Edge ids are deterministic.**" paragraph currently documents "a variant may declare `[Id]` … to assign one explicitly" and "Endpoints still unset when the id is first read fall back to a random Ulid mint". Replace those two sentences with:

> Edge identity is canonical and closed: `[Id]` on a relation variant is a compile error (CG057), and reading `Id` before both endpoints are set throws `InvalidOperationException`. The id a variant carries is therefore always `RecordId.ForEdge(in, edge, out)` (or the hydrated row id, which is the same value for rows this library wrote). Hand-minted edge rows outside the variant save path can still use `RecordId.Idempotent`/`Resolve`.

- [ ] **Step 6: Full run + commit**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj` → all green (477 + 1 new = **478**).

```bash
git add src/Disruptor.Surface.Generator/Emit/RelationVariantEmitter.cs tests/ docs/api.md docs/notes.md
git commit -m "fix(generator): __MintId throws on unresolvable endpoints — no random edge ids

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `SurrealSession.GetAll<T>()`

Review finding 2: the documented hydration flow (`quickstart.md:288/292`, `api.md:484/487`, `HydrationQuery.cs:18` XML) calls `session.GetAll<T>()`, which doesn't exist. Add it; the three doc sites become correct with zero edits.

**Files:**
- Modify: `src/Disruptor.Surface.Runtime/SurrealSession.cs` (insert directly after `Get<T>`, lines 307–312)
- Modify: `tests/Disruptor.Surface.Tests/Runtime/SurrealSessionTests.cs` (two new tests + one line in `ClosedSession_Reads_Throw`, lines 54–67)
- Modify: `docs/api.md` (one row in the "Read methods (sync)" table after line 1023)

**Interfaces:**
- Consumes: `state.Entities` (`Dictionary<RecordId, IEntity>`), `ThrowIfClosed()`, `RecordId.CompareTo` (ordinal Table-then-Value, `RecordId.cs:109–117`).
- Produces: `public IReadOnlyCollection<T> GetAll<T>() where T : class, IEntity` — deterministic id-ordered snapshot; quickstart/api hydration examples compile against it.

- [ ] **Step 1: Write the failing tests.** Mirror `Get_ReturnsTypedEntity_WhenTracked` (SurrealSessionTests.cs:210–229). First read the test doubles at lines 1553+ — the type-filter test needs a second `IEntity` double (`RefStubEntity` or `StubVariant`; use whichever ctor is simplest, shown here as `RefStubEntity(RecordId)` — match its actual signature):

```csharp
[Fact]
public void GetAll_ReturnsOnlyMatchingType_OrderedById()
{
    var session = new SurrealSession();
    var b = new StubEntity(new RecordId("designs", "bb"));
    var a = new StubEntity(new RecordId("designs", "aa"));
    var other = new RefStubEntity(new RecordId("constraints", "cc"));
    ((IHydrationSink)session).Track(b);
    ((IHydrationSink)session).Track(other);
    ((IHydrationSink)session).Track(a);

    var all = session.GetAll<StubEntity>();

    Assert.Equal(new[] { a, b }, all);            // id-ordered, other type filtered out
    Assert.Empty(session.GetAll<RefStubEntity>().Where(e => e.Id.Table == "designs"));
}

[Fact]
public void GetAll_EmptySession_ReturnsEmpty()
{
    var session = new SurrealSession();
    Assert.Empty(session.GetAll<StubEntity>());
}
```

And in `ClosedSession_Reads_Throw` (lines 54–67), add alongside the existing `Get` probe:

```csharp
Assert.Throws<InvalidOperationException>(() => session.GetAll<StubEntity>());
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj --filter "FullyQualifiedName~SurrealSessionTests.GetAll"`
Expected: FAIL with CS1061 (`GetAll` not defined) — a compile failure of the test project counts as the red step here.

- [ ] **Step 3: Implement**, directly after `Get<T>`:

```csharp
/// <summary>
/// All tracked entities of type <typeparamref name="T"/> in this session's identity
/// map, ordered by id (ordinal table-then-value) for deterministic iteration. The
/// batch-mutate companion to <see cref="Get{T}(IRecordId)"/> — pairs with the
/// hydration terminal (<c>Workspace.Hydrate.{Table}(ids)</c>), whose ExecuteAsync
/// returns only the populated session.
/// </summary>
public IReadOnlyCollection<T> GetAll<T>() where T : class, IEntity
{
    ThrowIfClosed();
    var results = new List<T>();
    foreach (var entity in state.Entities.Values)
    {
        if (entity is T typed)
        {
            results.Add(typed);
        }
    }
    results.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    return results;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj`
Expected: all green (**480**).

- [ ] **Step 5: Docs + commit.** Add to `docs/api.md` read-methods table after the `Get<T>` row (line 1023):
`| \`GetAll<T>()\` | All tracked entities of type \`T\`, ordered by id — the batch-mutate companion for hydration flows. |`

```bash
git add src/Disruptor.Surface.Runtime/SurrealSession.cs tests/Disruptor.Surface.Tests/Runtime/SurrealSessionTests.cs docs/api.md docs/notes.md
git commit -m "feat(runtime): SurrealSession.GetAll<T>() — id-ordered typed snapshot of the identity map

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Stale-docs cleanup (review finding 3)

All sites verified 2026-07-03; each edit below is mechanical. No test cycle (comments/docs only) beyond a build to catch XML cref errors.

**Files:** as listed per step.

- [ ] **Step 1:** `src/Disruptor.Surface.Runtime/SurrealSession.cs:835–853` — delete the entire first (orphaned) `<summary>` block on `SaveAsync` (the streamed-txn / closes-on-return / one-shot text). The accurate summary at 854–868 stays. `DeleteAsync`/`UnrelateAsync` docs are accurate — leave them.
- [ ] **Step 2:** `src/Disruptor.Surface.Runtime/Query/SurfaceProjection.cs:23–25` — "runs once per result row with a real JSON-backed row" → "runs once per result row against the CBOR-decoded <see cref=\"Disruptor.Surreal.Values.SurrealObjectValue\"/>". Lines 80–82 — replace the dangling `<see cref="JsonProjectionRow"/>` with `<see cref="ValueProjectionRow"/>` and "hit the real JSON values" → "hit the decoded response values".
- [ ] **Step 3:** `src/Disruptor.Surface.Runtime/Query/IProjectionRow.cs:12–15` — dangling `JsonProjectionRow` cref → `ValueProjectionRow`; "reads each value out of the response JSON" → "reads each value out of the decoded response row". Lines 23–24 — "underlying JSON element" → "underlying <see cref=\"Disruptor.Surreal.Values.SurrealObjectValue\"/>".
- [ ] **Step 4:** `src/Disruptor.Surface.Runtime/Query/PropertyExpr.cs:11–13` — "serialises each binding into the JSON-RPC payload" → "carries each binding in the typed CBOR payload".
- [ ] **Step 5:** `src/Disruptor.Surface.Runtime/Query/IIncludeNode.cs:48–49` — "runs IEntity.Hydrate against the row JSON" → "against the row's <see cref=\"Disruptor.Surreal.Values.SurrealValue\"/>". `src/Disruptor.Surface.Runtime/Query/SurfaceQuery.cs:522–523` — "expands to a JSON array under the child-table alias" → "expands to a value array under the child-table alias".
- [ ] **Step 6: README vs quickstart.** Check what NuGet actually has:

Run: `curl -s https://api.nuget.org/v3-flatcontainer/disruptor.surface.runtime/index.json`
- If it returns a version list (expected — tags v0.1.0-preview.52…59 all triggered `release.yml`): rewrite `README.md:7`'s "**Package status:** not yet published to NuGet…" to "**Package status:** published to NuGet as `Disruptor.Surface.Runtime` + `Disruptor.Surface.Generator` (`0.1.0-preview.*`, see [quickstart](docs/quickstart.md)); building from a checkout also works (see [Building](#building))."
- If it 404s: leave README, and in `docs/quickstart.md:11` mark the PackageReference block "once packages are published — until then use the ProjectReference form below".
- [ ] **Step 7 (optional, 15 min):** `docs/memory/` still names the preview.51-deleted `RelateAsync` as current (`feedback_async_is_methods.md:12`, `project_aggregates.md:3`, `project_concurrency_model.md:7`, `docs/memory/MEMORY.md` lines 9/14). Update those lines to `session.SaveAsync(new TVariant { Source, Target }, tx)` / `UnrelateAsync`. Accurate negations ("no JSON bridge", "no lease") and `notes.md` historical log entries stay untouched.
- [ ] **Step 8: Build + full test + commit**

Run: `dotnet build Disruptor.Surface.slnx` then `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj`
Expected: clean build (no CS1574 cref warnings), 480/480.

```bash
git add -A
git commit -m "docs: remove stale streamed-txn/JSON-era comments; reconcile README package status

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Live-smoke harness — sample additions

The sample currently covers only item 5 (happy path) and the CREATE half of item 7 of the `remaining-work.md` §1 table. Extend it so one `dotnet run` exercises all seven shapes plus the identifier-quoting probe. Two model additions are needed: an *optional* scalar for the NONE semantics (items 1/5) and a nullable variant payload for the duplicate-update path (item 4).

**Files:**
- Modify: `src/Disruptor.Surface.Sample/Models/Constraint.cs` — add `[Property] public partial string? Notes { get; set; }`
- Modify: `src/Disruptor.Surface.Sample/Relations/Variants/ReviewAssessesDesign.cs` — add `[Property] public partial string? Note { get; set; }` (single-variant kind → SCHEMAFULL edge table + `ON DUPLICATE KEY UPDATE note = $_p_note` path, the exact shape smoke item 4 needs)
- Modify: `src/Disruptor.Surface.Sample/Program.cs` — new sections `(8) Release smoke` and `(9) Identifier-quoting probe` invoked after the existing demos

**Interfaces:**
- Consumes: `Workspace.Query.Constraints.Where(ConstraintQ.…)` roots + terminals exactly as `DemoQueryLayer` uses them (Program.cs:354–530 — read first and mirror the terminal calls verbatim); `workspace.LoadDesignAsync(tx|db, id)` / `LoadReviewAsync`; `session.SaveAsync(entity|IEnumerable, tx)`; `RecordId.ForEdge`; raw SQL via the same `db`-level query call `ApplySchemaAsync` uses (check the emitted `Workspace.Schema.g.cs` for the exact SDK method) and `tx.QueryAsync` for in-txn statements.
- Produces: console output `smoke[N] PASS/FAIL — detail` per item and `quoting[position] OK/FAILED — error` per probe; Task 6 records these.

- [ ] **Step 1: Model additions** (both one-liners above). Build the sample to regenerate:

Run: `dotnet build src/Disruptor.Surface.Sample/Disruptor.Surface.Sample.csproj`
Expected: clean; inspect `obj/Debug/net10.0/generated/...` — `ReviewAssessesDesign` save body now contains `ON DUPLICATE KEY UPDATE note = $_p_note` and the schema chunk gains `DEFINE FIELD IF NOT EXISTS notes ON constraints TYPE option<string>;`.

- [ ] **Step 2: Write `DemoReleaseSmoke`.** Add to `Program.cs` (local-function style matching `DemoQueryLayer`; a tiny `Report(string item, bool pass, string detail)` local that prints and collects failures). The seven checks — adapt query terminals to the file's existing idiom:

```csharp
// (1) Eq(null)/IsNone matches rows whose field was omitted at save time
//     Seed two constraints on a fresh design: one with Notes set, one untouched.
//     Query IsNone → only the unset one; Eq(null) → same result.
// (2) [Version]-guarded UPDATE returns empty on mismatch → SurrealVersionConflictException
//     Load a session read-mode (stale snapshot), commit a bump through a separate tx
//     (LoadDesignAsync(tx2) → mutate → SaveAsync → Commit), then SaveAsync the stale
//     entity in a fresh tx and expect SurrealVersionConflictException. Also record
//     (don't fail on) the two-concurrent-transactions variant: load both sessions
//     inside open txs, commit A, save B — note WHICH exception surfaces
//     (SurrealVersionConflictException vs the MVCC SurrealConflictException at commit);
//     that answer goes in the validation doc.
// (3) Bulk save: three new Constraints (same table, contiguous) through
//     session.SaveAsync(new IEntity[]{ c1, c2, c3 }, tx) in one tx that ALSO re-saves
//     the (already-tracked) parent Design first — asserts the coalesced
//     INSERT INTO path and that a same-batch child sees the earlier statement.
//     Reload after commit and count the three descriptions.
// (4) Deterministic edge duplicate path: save ReviewAssessesDesign{Source=r, Target=d,
//     Note="first"}; save again with Note=null. Assert both saves carry the same
//     ((IEntity)e).Id == RecordId.ForEdge("assesses", r, d); then raw-select the row
//     and assert note IS NONE (the SurrealValue.None / CBOR tag 6 duplicate binding).
// (5) NONE-guarded string functions skip unset rows: with the mixed Notes seed from
//     (1), run ConstraintQ.Notes.Contains("smoke") — must NOT error the SELECT and
//     must return only the set row.
// (6) string::matches: ConstraintQ.Description.Matches("^seed-[0-9]+$") returns the
//     seeded constraints and nothing else.
// (7) Audit round-trip: capture CreatedAtUtc/UpdatedAtUtc/Version on a loaded Design;
//     mutate Description; SaveAsync + commit; reload fresh → CreatedAtUtc unchanged,
//     UpdatedAtUtc advanced, Version == before + 1.
```

Each check is straight-line code against the existing sample models — no new abstractions. On FAIL, print and continue (the pass must report all seven verdicts in one run). Exit code 1 if any failed.

- [ ] **Step 3: Write `ProbeIdentifierQuoting`.** Raw statements through the same db-level query call `ApplySchemaAsync` uses (DDL may be rejected inside a transaction — run these outside one). One statement per emit position `remaining-work.md` §2 names, each printed OK/FAILED:

```csharp
string[] probes =
[
    // DEFINE FIELD with reserved-word names (the `none` case is the killer)
    "DEFINE TABLE IF NOT EXISTS quote_probe SCHEMAFULL;",
    "DEFINE FIELD IF NOT EXISTS `order` ON quote_probe TYPE string;",
    "DEFINE FIELD IF NOT EXISTS `none` ON quote_probe TYPE option<string>;",
    // DEFINE INDEX columns
    "DEFINE INDEX IF NOT EXISTS idx_quote_probe ON TABLE quote_probe COLUMNS `order`;",
    // CREATE + SET position
    "CREATE quote_probe:one SET `order` = 'a', `none` = 'x';",
    // WHERE + ORDER BY
    "SELECT * FROM quote_probe WHERE `order` = 'a' ORDER BY `order` ASC;",
    // UPDATE SET (the ON-DUPLICATE assignment position is grammatically SET)
    "UPDATE quote_probe:one SET `none` = 'y';",
    // subselect alias
    "SELECT *, (SELECT id FROM quote_probe) AS `group` FROM quote_probe;",
    // control: UNQUOTED reserved word — document the failure mode quoting fixes
    "SELECT * FROM quote_probe WHERE none = 'x';",
];
```

The control probe's result is informative either way: if it errors or silently matches nothing, that's the evidence line for the quoting decision.

- [ ] **Step 4: Build; commit**

Run: `dotnet build src/Disruptor.Surface.Sample/Disruptor.Surface.Sample.csproj`
Expected: clean (this task is only compile-verifiable without a server; Task 6 runs it).

```bash
git add src/Disruptor.Surface.Sample/
git commit -m "feat(sample): release-smoke section covering the 7 live-validation shapes + identifier-quoting probe

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Run the live pass, record results, fix fallout

- [ ] **Step 1: Start an ephemeral server** (memory backend — fresh DB, which is also the sanctioned answer to the pre-existing-data question):

Run (background): `surreal start --bind 127.0.0.1:8000 --default-namespace project-brain --default-database workspace --username root --password secret`

- [ ] **Step 2: Run the harness**

Run: `dotnet run --project src/Disruptor.Surface.Sample`
Expected: existing sections 1–7 complete as before; `smoke[1..7] PASS`; quoting probes print per-position verdicts.

- [ ] **Step 3: If a smoke item FAILS** — per `remaining-work.md`: *the pinned tests encode the intended shape; fix the compile/emit, not the test's intent.* The traceback table (pinned test per item): item 1/5/6 → `Runtime/Query/SurfaceQueryCompilerTests.cs` (lines 370–411 / 248–291 / 463–471); item 2/7 → `Generator/AuditConcurrencyTests.cs` (51–140 / 64–127); item 3 → `Generator/BulkSaveTests.cs` (136–305, visibility argument at `SurrealSession.cs:901–910`); item 4 → `Generator/VariantSaveTests.cs` (134–162). Any fix follows red-green against the pinned test, then re-run this task from Step 2.
- [ ] **Step 4: Write `docs/live-validation-2026-07-03.md`**: a 7-row verdict table (item, observed behavior, PASS/FAIL, fix commit if any), the item-2 exception-ordering observation (version-guard vs MVCC conflict), and a quoting section: per-position verdict + the control-probe evidence + **recommendation** (backticks accepted everywhere → implement quoting at the `SurrealFormatter.Identifier()` chokepoint + the four codegen-baked emitters as the immediate follow-up PR; any position rejecting backticks → fall back to a reserved-word diagnostic instead).
- [ ] **Step 5: Update the trackers**: `Improvements.md` items 1–3 (validated / re-scoped per results) and `remaining-work.md` §1 (mark the table run, link the validation doc) + §2 (record the quoting verdict; the owner decision is now informed).
- [ ] **Step 6: Commit**

```bash
git add docs/live-validation-2026-07-03.md Improvements.md docs/remaining-work.md docs/notes.md
git commit -m "docs: live-substrate validation results (7-item smoke + identifier-quoting probe)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Release `v0.1.0-preview.60`

**Preconditions:** Tasks 1–6 committed; full suite green; live validation doc shows 7× PASS (or documented+fixed).

- [ ] **Step 1: PR + merge.** Push `claude/over-the-line`, open a PR (body summarises: review findings 1–3 closed, live pass results, CG057), get it onto `master`.
- [ ] **Step 2: `docs/notes.md` release grooming** (on master, or in the PR): retitle the ~10 `### unreleased — … (DONE 2026-07-02/03)` engineering-log headers to `### preview.60 — …`, including this plan's entries. Add the pre-existing-data note to the preview.60 entry: *edges written before deterministic ids (pre-preview.60 rows) carry random Ulid ids; replaying a save against them updates the old row while the session derives the hash id — wipe/reseed dev databases when upgrading (preview-status policy).*
- [ ] **Step 3: Version bump.** `Directory.Build.props:13` → `<Version>0.1.0-preview.60</Version>`. Commit both:

```bash
git add Directory.Build.props docs/notes.md
git commit -m "release: 0.1.0-preview.60

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

- [ ] **Step 4: CONFIRM WITH OWNER, then tag.** Tagging publishes to NuGet (`release.yml`: build → test → pack → push). Do not push the tag without an explicit go-ahead in-session.

```bash
git tag v0.1.0-preview.60
git push origin master v0.1.0-preview.60
```

Expected: release workflow green; `Disruptor.Surface.*` `0.1.0-preview.60` on NuGet.

---

## Owner decisions (recommendations encoded; veto before/during execution)

| # | Question (`remaining-work.md` §4) | Recommendation baked into this plan |
|---|---|---|
| 1 | Identifier quoting: quote-everything vs reserved-word diagnostic | **Probe now (Task 5/6), implement after preview.60.** If backticks pass in every position, do full quoting as the immediate next PR (preview.61) — it touches every pinned SQL test and shouldn't ride the release PR. If any position rejects backticks, ship the reserved-word diagnostic instead. |
| 2 | Non-nullable variant payload left at null backing default (duplicate-update path) | **Save-time throw** (fail-closed, matches house style) — *not in this plan's tasks*; small follow-up alongside the quoting decision. `DEFAULT ""` hides bugs; "leave it" keeps a latent unbound-variable failure. |
| 3 | Pre-existing random-Ulid edge rows vs deterministic ids | **Wipe/reseed dev databases is the answer at preview status**; documented in the preview.60 notes entry (Task 7 Step 2). No migration tooling. Tasks 1–2 close the last *new* sources of non-canonical ids. |
| 4 | CG056 severity (unmapped variant payload: warn vs error) | **Keep Warning.** The fail-soft in-memory-only contract was deliberately designed this cycle and is coherent; the entity-table equivalent (CG025) stays the error. Revisit only if a user actually ships a silent non-persisting payload. |
| 5 | `IsNone()` contract (unset-or-null vs strict split) | **Confirm current** (matches `Eq(null)`; non-library writers may store explicit NULLs). `IsUnset()`/`IsNullValue()` splits go to the backlog on demand. |
| 6 | Update-by-query setter design | Backlog. Delete-by-query needn't wait for it (also backlog — not this plan). |
| 7 | `[Version]` starts at 1 / audit markers require explicit `[Property]` (CG052) | **Confirm as shipped.** |
| 8 | `ExistsAsync`/`CountAsync` not mirrored onto `ProjectionQuery` | **Confirm as shipped** (shape-independent; call before `.Select`). |

## Verification ledger (what "over the line" means, checkable)

1. `dotnet test …` ≥ 480 passing, 0 skipped, incremental-cache tests included.
2. Review findings 1–3 of `docs/review-main-2026-07-02.md` each closed by a named commit.
3. `docs/live-validation-2026-07-03.md` exists with 7× PASS and a quoting verdict.
4. `v0.1.0-preview.60` tag pushed **after owner confirmation**; release workflow green.
