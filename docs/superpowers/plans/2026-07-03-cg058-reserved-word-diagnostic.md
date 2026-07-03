# CG058/CG059 — SurrealQL Reserved-Word Diagnostic Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Catch, at compile time, any user model name that renders to a SurrealQL reserved word — a hard **error** for the four value literals that misparse *silently* (`none`/`null`/`true`/`false`), a **warning** for the other reserved keywords that fail *loudly* at schema-apply/query time and would be rescued by future backtick-quoting.

**Architecture:** Two `DiagnosticDescriptor`s (CG058 Error, CG059 Warning) + a static two-set reserved-word table sourced from SurrealDB v3.1.4's `RESERVED_KEYWORD` set, checked in `ModelValidation` (the position-independent `PendingDiagnostic` + declaration-location path that CG057 uses) over every render point where a user-controlled C# name becomes a SurrealQL identifier: entity fields, inline element sub-fields, table names, edge names, and relation-variant payload fields. No runtime change, no emitter change, no `Model/` change — so zero incremental-cache risk.

**Tech Stack:** .NET 10 / C# Roslyn incremental source generator (netstandard2.0), xUnit.

**Why this scope (validated 2026-07-03):** An adversarial validation of the identifier-quoting claim confirmed the gap is real and reachable but that the "quote every emitted statement" remediation is unproven-necessary and *dangerous without a reject step* (backtick-quoting `none` is accepted at DDL then silently poisons reads — a loud→silent regression). The minimal correct fix is reject-only. The reserved-word list was then re-grounded against SurrealDB's actual parser source (`crates/core/src/syn/lexer/keywords.rs` @ `v3.1.4`), which is narrower and more accurate than the live-validation doc's inferred list. Backtick-quoting soft names remains an **optional, deferred** follow-up (and, if it ever lands, must escape iff-in-`RESERVED_KEYWORD`, mirroring SurrealDB's `EscapeIdent`, and must never ship without this diagnostic).

## Global Constraints

- Test command: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj` — baseline is **480/480 green, 0 skipped** (master @ preview.60). Expected end state: 480 + new tests (see Task 1), 0 regressions. The fixture audit confirmed **zero existing fixtures collide**, so nothing existing turns red.
- Any diagnostic added to `ModelValidation` must be a position-independent `PendingDiagnostic` (location resolved through the declaration-location map at report time — never store `Location`/`ISymbol`/`SyntaxNode` in a model). The `trackIncrementalGeneratorSteps` cache regression tests must stay green.
- Diagnostic ids: CG001–CG057 are taken; **CG058 and CG059 are the next free ids**. Category constant is `"Disruptor.Surface"` (`Pipeline/Diagnostics.cs`).
- Do not write to `CLAUDE.md`; project documentation goes in `docs/notes.md` (maintain the engineering log, newest first, `### unreleased — … (DONE 2026-07-03)` style).
- Branch off `master`; commit per task; the release/merge decision is the owner's (do **not** tag or bump the version in this plan).
- The name-rendering rule (verified): `SurrealNaming.ToFieldName(name)` = Humanizer `Underscore()` (snake_case **and** lowercase); `ToTableName` = `Pluralize().Underscore()`; `ToEdgeName` = `StripAttributeSuffix().Underscore()`. All lowercase the output, so the diagnostic compares the rendered lowercase name against lowercase word sets. `ModelValidation` already `using`s `Disruptor.Surface.Generator.Emit` and calls `SurrealNaming` statics, and `RelationLinker.ComputeNameCollisions` (CG042–CG044) is the canonical "render the user name with `SurrealNaming`, diagnose on the rendered string" pattern to mirror.

### The reserved-word sets (load-bearing — copy verbatim)

Source: `RESERVED_KEYWORD` `phf::Set` in `surrealdb/crates/core/src/syn/lexer/keywords.rs` @ tag `v3.1.4` (44 words; this set is exactly what SurrealDB's `EscapeIdent` serializer backtick-quotes). Matching is ASCII case-insensitive; our renderer lowercases, so compare lowercase-to-lowercase.

**VALUE_LITERALS (CG058, Error — silent misparse):** these four are intercepted as literals in expression position *before* the identifier fallback (`parse_prime_expr`), so a bare occurrence in a generated `WHERE`/`SET`/projection silently becomes the literal, not a field reference — no error raised, wrong results:
```
none  null  true  false
```

**RESERVED_KEYWORDS (CG059, Warning — loud, rescuable):** the remaining 40 words of `RESERVED_KEYWORD`. Emitted bare they raise a loud parse/apply error and are fully rescued by backtick-quoting:
```
after all alter before begin break by cancel commit continue create define
delete diff for function if info insert kill let live option rand rebuild
relate remove return select sequence show sleep table tb throw update upsert
use value where
```

Words the live-validation doc *assumed* were reserved but the parser source proves are **NOT** (do **not** warn on these — over-warning `Type`/`Order`/`Count` would be false positives): `order group limit start set content fetch split count type default in out and or not contains is on as then else end`. (`in`/`out` are the built-in edge-endpoint field names by convention, not lexer-reserved; out of scope here.)

---

### Task 1: CG058 (Error) + CG059 (Warning) reserved-word diagnostic

**Files:**
- Create: `src/Disruptor.Surface.Generator/Pipeline/SurrealReservedWords.cs` (the two static sets + source citation)
- Modify: `src/Disruptor.Surface.Generator/Pipeline/Diagnostics.cs` (append CG058 + CG059 descriptors after CG057)
- Modify: `src/Disruptor.Surface.Generator/Pipeline/ModelValidation.cs` (new `ValidateReservedWords` pass called from the main `Validate`)
- Test: `tests/Disruptor.Surface.Tests/Generator/DiagnosticsTests.cs` (new tests)
- Modify: `docs/notes.md` (engineering-log entry + Diagnostics section range → CG059), `docs/api.md` (Diagnostics table rows)

**Interfaces:**
- Consumes: `ModelValidation.Add(pending, DiagnosticDescriptor, string[] args, string? typeKey, string? memberName, LocationInfo? exactLocation)`; `SurrealNaming.ToFieldName/ToTableName/ToEdgeName`; the model graph's tables, relation kinds, and relation variants; the same "does this property emit a DEFINE FIELD" predicate `SchemaEmitter` uses to decide which properties render a column (reuse it — do not re-derive — so the check covers exactly the emitted fields: scalar `[Property]`, inline element collections, `[Reference]`, `[Parent]`, `[Children]`; excludes `[Id]` and relation read-collections).
- Produces: `Diagnostics.ReservedValueLiteralName` (CG058) and `Diagnostics.ReservedKeywordName` (CG059).

- [ ] **Step 1: Write the failing tests** in `DiagnosticsTests.cs`, mirroring the structure of `CG057_IdOnRelationVariant_IsRejected` (same `GeneratorHarness.Run` + `runDiags.FirstOrDefault(d => d.Id == …)` idiom; pass `FixturePath` only on the location-asserting ones):

```csharp
[Fact]
public void CG058_ValueLiteralFieldName_IsRejected()
{
    // A [Property] named None renders to the SurrealQL identifier `none`, a value
    // literal that silently misparses in WHERE/SET — hard error.
    const string src = """
        using Disruptor.Surface.Annotations;
        namespace M;
        [Table] public partial class Widget {
            [Id] public partial WidgetId Id { get; set; }
            [Property] public partial string None { get; set; }
        }
        [CompositionRoot] public partial class Workspace { }
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src, FixturePath);
    var diag = runDiags.FirstOrDefault(d => d.Id == "CG058");
    Assert.NotNull(diag);
    Assert.Equal(DiagnosticSeverity.Error, diag!.Severity);
    Assert.Contains("none", diag.GetMessage());
    Assert.Contains("None", diag.GetMessage());          // names the C# member
    AssertLocationAt(diag, src, "string None");
}

[Fact]
public void CG058_ValueLiteralEdgeName_IsRejected()
{
    // Edge name derives from the relation-kind attribute: NoneAttribute -> `none`.
    const string src = """
        using Disruptor.Surface.Annotations;
        namespace M;
        public sealed class NoneAttribute : ForwardRelation;
        [Table] public partial class A { [Id] public partial AId Id { get; set; } }
        [None] public partial class Link {
            [In] public partial A Source { get; set; }
            [Out] public partial A Target { get; set; }
        }
        [CompositionRoot] public partial class Workspace { }
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src);
    var diag = runDiags.FirstOrDefault(d => d.Id == "CG058");
    Assert.NotNull(diag);
    Assert.Equal(DiagnosticSeverity.Error, diag!.Severity);
    Assert.Contains("none", diag.GetMessage());
}

[Fact]
public void CG059_ReservedKeywordFieldName_Warns()
{
    // A [Property] named Value renders to `value`, which is in RESERVED_KEYWORD:
    // fails loudly at schema-apply, rescuable by future quoting -> warning.
    const string src = """
        using Disruptor.Surface.Annotations;
        namespace M;
        [Table] public partial class Widget {
            [Id] public partial WidgetId Id { get; set; }
            [Property] public partial string Value { get; set; }
        }
        [CompositionRoot] public partial class Workspace { }
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src, FixturePath);
    var diag = runDiags.FirstOrDefault(d => d.Id == "CG059");
    Assert.NotNull(diag);
    Assert.Equal(DiagnosticSeverity.Warning, diag!.Severity);
    Assert.Contains("value", diag.GetMessage());
    AssertLocationAt(diag, src, "string Value");
}

[Fact]
public void CG058_And_CG059_DoNotFire_ForNonReservedNames()
{
    // Corrects the live-validation doc's assumption: order/group/type/count/status
    // are NOT in RESERVED_KEYWORD and must NOT warn.
    const string src = """
        using Disruptor.Surface.Annotations;
        namespace M;
        [Table] public partial class Widget {
            [Id] public partial WidgetId Id { get; set; }
            [Property] public partial int Order { get; set; }
            [Property] public partial string Group { get; set; }
            [Property] public partial string Type { get; set; }
            [Property] public partial int Count { get; set; }
            [Property] public partial string Status { get; set; }
        }
        [CompositionRoot] public partial class Workspace { }
        """;
    var (_, _, runDiags, _) = GeneratorHarness.Run(src);
    Assert.DoesNotContain(runDiags, d => d.Id == "CG058");
    Assert.DoesNotContain(runDiags, d => d.Id == "CG059");
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj --filter "FullyQualifiedName~CG058|FullyQualifiedName~CG059"`
Expected: the three positive tests FAIL (no CG058/CG059 reported); the negative passes vacuously.

- [ ] **Step 3: Create the reserved-word table** `src/Disruptor.Surface.Generator/Pipeline/SurrealReservedWords.cs`:

```csharp
using System.Collections.Generic;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// SurrealQL reserved identifiers, split by failure mode. Sourced from the
/// <c>RESERVED_KEYWORD</c> set in <c>surrealdb/crates/core/src/syn/lexer/keywords.rs</c>
/// @ tag <c>v3.1.4</c> — the exact set SurrealDB's own <c>EscapeIdent</c> serializer
/// backtick-quotes. Compared against the rendered (snake_cased, lowercased) identifier.
/// If the pinned SurrealDB version is bumped, re-pull that file — the set grows across releases.
/// </summary>
internal static class SurrealReservedWords
{
    /// <summary>Value literals intercepted before the identifier fallback in
    /// <c>parse_prime_expr</c>: a bare occurrence silently becomes the literal, not a
    /// field reference (no error). CG058 (Error).</summary>
    public static readonly HashSet<string> ValueLiterals = new(System.StringComparer.Ordinal)
    {
        "none", "null", "true", "false",
    };

    /// <summary>The remaining 40 words of <c>RESERVED_KEYWORD</c>: emitted bare they raise
    /// a loud parse/apply error and are fully rescued by backtick-quoting. CG059 (Warning).</summary>
    public static readonly HashSet<string> ReservedKeywords = new(System.StringComparer.Ordinal)
    {
        "after", "all", "alter", "before", "begin", "break", "by", "cancel", "commit",
        "continue", "create", "define", "delete", "diff", "for", "function", "if", "info",
        "insert", "kill", "let", "live", "option", "rand", "rebuild", "relate", "remove",
        "return", "select", "sequence", "show", "sleep", "table", "tb", "throw", "update",
        "upsert", "use", "value", "where",
    };
}
```

- [ ] **Step 4: Add the descriptors** in `Diagnostics.cs`, after `IdOnRelationVariant` (CG057), matching that declaration style:

```csharp
public static readonly DiagnosticDescriptor ReservedValueLiteralName = new(
    id: "CG058",
    title: "Generated identifier collides with a SurrealQL value literal",
    messageFormat: "'{0}' renders to the SurrealQL identifier '{1}', which is a reserved value literal (none/null/true/false). Emitted bare in a query it parses as the literal — silently matching no rows and poisoning reads of the column — and backtick-quoting does not rescue it. Rename the member so it renders to a non-reserved identifier.",
    category: Category,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);

public static readonly DiagnosticDescriptor ReservedKeywordName = new(
    id: "CG059",
    title: "Generated identifier is a SurrealQL reserved keyword",
    messageFormat: "'{0}' renders to the SurrealQL identifier '{1}', which is a reserved keyword. The generator emits identifiers unquoted, so the schema DDL or a query using it will fail at apply/query time. Rename the member (the generator does not yet backtick-quote reserved identifiers).",
    category: Category,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true);
```

- [ ] **Step 5: Implement the check** in `ModelValidation.cs` — a new method called from the main `Validate` (near where CG042–CG044 collisions and the per-table/per-property loops run). Reuse the existing "does this property emit a DEFINE FIELD" predicate from `SchemaEmitter` (find it — it is how `SchemaEmitter` decides which `PropertyModel`s become `DEFINE FIELD` lines; do not re-derive the kind test) so coverage matches emitted fields exactly:

```csharp
// CG058/CG059 — reserved-word collision on any user-controlled name that renders to a
// SurrealQL identifier. Value literals (none/null/true/false) misparse silently -> error;
// the other RESERVED_KEYWORD words fail loudly and are rescuable by future quoting -> warning.
private static void ValidateReservedWords(ModelGraph graph, ImmutableArray<PendingDiagnostic>.Builder pending)
{
    foreach (var table in graph.Tables)
    {
        // Table name (pluralized; rarely reserved, checked for completeness).
        CheckName(pending, SurrealNaming.ToTableName(table.Name),
            display: table.Name, typeKey: table.FullName, memberName: null);

        foreach (var p in table.Properties.Where(EmitsSchemaField))   // reuse SchemaEmitter's predicate
        {
            CheckName(pending, SurrealNaming.ToFieldName(p.Name),
                display: $"{table.Name}.{p.Name}", typeKey: table.FullName, memberName: p.Name);

            foreach (var im in p.InlineMembers)                        // array<object> sub-fields
                CheckName(pending, SurrealNaming.ToFieldName(im.Name),
                    display: $"{table.Name}.{p.Name}.{im.Name}", typeKey: table.FullName, memberName: p.Name);
        }
    }

    foreach (var kind in graph.ForwardRelationKinds)                  // edge names
        CheckName(pending, SurrealNaming.ToEdgeName(kind.Name),
            display: kind.Name, typeKey: kind.FullName, memberName: null);

    foreach (var variant in graph.RelationVariants)                   // variant payload fields
        foreach (var p in variant.PayloadProperties)
            CheckName(pending, p.FieldName,                           // precomputed on the model
                display: $"{variant.Name}.{p.Name}", typeKey: variant.FullName, memberName: p.Name);
}

private static void CheckName(
    ImmutableArray<PendingDiagnostic>.Builder pending, string rendered,
    string display, string typeKey, string? memberName)
{
    if (SurrealReservedWords.ValueLiterals.Contains(rendered))
        Add(pending, Diagnostics.ReservedValueLiteralName, args: [display, rendered], typeKey: typeKey, memberName: memberName);
    else if (SurrealReservedWords.ReservedKeywords.Contains(rendered))
        Add(pending, Diagnostics.ReservedKeywordName, args: [display, rendered], typeKey: typeKey, memberName: memberName);
}
```

**Implementer note:** the exact model member names above (`graph.Tables`, `table.Properties`, `p.InlineMembers`, `graph.ForwardRelationKinds`, `graph.RelationVariants`, `variant.PayloadProperties`, `p.FieldName`, and the `EmitsSchemaField` predicate) are indicative — resolve each against the actual `Model/` records and `SchemaEmitter`/`ModelValidation` code before writing, and adjust names to match. The *contract* is fixed: check every rendered field/table/edge/inline/variant-payload identifier; skip `[Id]` and relation read-collections (they render no field); point the location at the member for field/payload hits and at the class for table/edge hits. Wire `ValidateReservedWords(graph, pending)` into the same `Validate` entry point that runs the other located checks.

- [ ] **Step 6: Run the new tests**

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj --filter "FullyQualifiedName~CG058|FullyQualifiedName~CG059"`
Expected: 4/4 PASS.

- [ ] **Step 7: Full suite** (the fixture audit found zero collisions, so nothing existing should regress; confirm)

Run: `dotnet test tests/Disruptor.Surface.Tests/Disruptor.Surface.Tests.csproj`
Expected: all green, count = 480 + 4 new. If any *existing* test newly fails, a fixture collided that the audit missed — STOP and report it (do not blanket-rename fixtures without confirming the collision is real).

- [ ] **Step 8: Docs** (feature documentation only — the evidence-correction is Task 2):
  - `docs/notes.md` "### Diagnostics" section: bump the range to `CG001`–`CG059` and append a highlight: `CG058 (error — generated identifier collides with a SurrealQL value literal none/null/true/false, which misparses silently), CG059 (warning — generated identifier is a reserved keyword; fails loudly, not yet backtick-quoted)`.
  - `docs/notes.md` engineering log: new `### unreleased — CG058/CG059 reserved-word diagnostics (identifier-quoting reject-only fix) (DONE 2026-07-03)` entry — two-tier rationale, the source-of-truth (`keywords.rs` @ v3.1.4), and that quoting stays deferred.
  - `docs/api.md` Diagnostics table: add `| \`CG058\` | Error — generated identifier renders to a SurrealQL value literal (none/null/true/false); misparses silently. |` and `| \`CG059\` | Warning — generated identifier is a SurrealQL reserved keyword; fails at apply/query time, not yet quoted. |`.

- [ ] **Step 9: Commit**

```bash
git add src/Disruptor.Surface.Generator/Pipeline/SurrealReservedWords.cs src/Disruptor.Surface.Generator/Pipeline/Diagnostics.cs src/Disruptor.Surface.Generator/Pipeline/ModelValidation.cs tests/Disruptor.Surface.Tests/Generator/DiagnosticsTests.cs docs/notes.md docs/api.md
git commit -m "feat(generator): CG058/CG059 reject SurrealQL reserved-word identifiers (two-tier)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Correct the reserved-word evidence in the trackers

The live-validation doc and `remaining-work.md` currently assert `order`/`group`/`value` are "soft reserved words rescued by backticks" and recommend the full hybrid (quote + reject). The parser-source grounding proves `order`/`group` are **not** reserved (the probe conflated "backticks work" with "backticks needed") and that reject-only is the correct minimal fix. Record the correction and the shipped decision so the follow-up quoting PR is scoped from facts, not the earlier inference.

**Files:**
- Modify: `docs/live-validation-2026-07-03.md` (the identifier-quoting section §3 + recommendation)
- Modify: `docs/remaining-work.md` (§2 deferred-fixes + §4 Q1 quoting-strategy question)
- Modify: `Improvements.md` (item 1)

**Interfaces:** none (docs only).

- [ ] **Step 1: Correct the live-validation doc.** In the identifier-quoting section, add a correction note (do not delete the recorded transcript — it stays as observed evidence; annotate its interpretation):
  - State that the authoritative reserved set is the 44-word `RESERVED_KEYWORD` in `keywords.rs` @ `v3.1.4` (= SurrealDB's own `EscapeIdent` set), and that `order`/`group`/`type`/`count`/`limit`/`start` are **not** in it — the probe showed backtick-quoted forms *work*, which does not imply the bare forms *need* quoting.
  - Reclassify: silent/unrescuable = `none`/`null`/`true`/`false` (value literals); loud/rescuable reserved = the other 40 incl. `value`/`select`/`where`/`table`; the earlier "soft = order/group/value" line was an over-broad inference.
  - Update the recommendation to: **shipped** — two-tier compile-time diagnostic (CG058 error on value literals, CG059 warning on `RESERVED_KEYWORD`); backtick-quoting is **deferred and optional**, and if it ever lands it must (a) escape iff-in-`RESERVED_KEYWORD` mirroring `EscapeIdent`, (b) never ship without CG058 (quoting a value literal converts a loud failure into silent read-poison), and (c) first be justified by the still-missing bare-soft-word live test.
- [ ] **Step 2:** `docs/remaining-work.md` §2 (identifier-quoting deferred fix) and §4 Q1 (quoting strategy owner question): mark Q1 **resolved** — owner chose reject-only two-tier (CG058/CG059), shipped; quoting deferred as optional robustness. Point at the live-validation correction.
- [ ] **Step 3:** `Improvements.md` item 1 (SurrealQL identifier quoting): update status to "reject-only diagnostic shipped (CG058/CG059); backtick-quoting deferred/optional," with the `keywords.rs` source reference.
- [ ] **Step 4: Build + commit** (docs only; a build catch guards api.md/notes.md cref sanity if any were added)

```bash
dotnet build Disruptor.Surface.slnx   # expect clean
git add docs/live-validation-2026-07-03.md docs/remaining-work.md Improvements.md
git commit -m "docs: correct reserved-word evidence to the SurrealDB v3.1.4 parser set; record reject-only decision

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Deferred (explicitly NOT in this plan)

- **Backtick-quoting soft identifiers** at `SurrealFormatter.Identifier()` + the four codegen-baked emitters. Optional robustness that preserves natural reserved-keyword names instead of warning on them. Necessity is unproven (no bare-soft-word live test exists) and it must never ship without CG058. If pursued: escape iff-in-`RESERVED_KEYWORD` (mirror `EscapeIdent`), add the missing bare-`DEFINE FIELD value` / bare-`WHERE value` live control, and de-confound `ProbeIdentifierQuoting` in the sample.
- The three test-hardening pins and the `docs/memory/` RelateAsync sweep from the preview.60 final review remain open follow-ups (unrelated to this fix).

## Self-review checklist (run before execution)

- Reserved-word sets copied verbatim from the Global Constraints block (44 words total; 4 error, 40 warning). ✓
- Every render point covered: field, inline sub-field, table, edge, variant payload; `[Id]`/read-collections excluded via the emitted-field predicate. ✓
- Location: member for fields/payloads, class for table/edge — via `typeKey`/`memberName`, position-independent. ✓
- No `Model/` change, no emitter change, no runtime change → cache tests unaffected. ✓
- Blast radius zero (audit) → no existing fixture edits; Step 7 halts if that proves wrong. ✓
