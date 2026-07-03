# Live-substrate validation — 2026-07-03

Runs the sample harness (`dotnet run --project src/Disruptor.Surface.Sample`)
against a live, ephemeral SurrealDB **3.1.4** (memory backend, fresh DB) and records
what the seven wire shapes from [`remaining-work.md` §1](remaining-work.md) and the
identifier-quoting probe (§2) actually do against a real substrate. This closes the
"none of it has touched a real SurrealDB v3" gap.

- **Server:** `surreal start --bind 127.0.0.1:8000 --default-namespace project-brain
  --default-database workspace --username root --password secret` (no path arg → memory
  backend; fresh DB, required so smoke item 6 counts a clean `constraints` table).
- **Harness commit:** `a9d7107` (`feat(sample): release-smoke section covering the 7
  live-validation shapes + identifier-quoting probe`). No harness change was needed —
  all seven smoke checks passed on the first run; the probe FAILEDs are genuine
  substrate behavior, not harness defects.
- **Harness exit code:** `0` (all 7 smoke checks PASSED; the process only exits 1 if a
  smoke check fails). Sections 1–7 (schema apply of 28 chunks, 10 seeded designs, review
  aggregate, query/traversal/variant-terminal demos) completed as before.

## 1. Release smoke — the seven §1 shapes

Verdicts below transcribe the harness's `smoke[N]` lines verbatim (observed output, not
expected). All seven PASSED; no compile/emit fallout, so no fix commits.

| # | Observed behavior (verbatim `smoke[N]` detail) | Verdict | Fix commit |
| --- | --- | --- | --- |
| 1 | `IsNone→1 row(s), Eq(null)→1 row(s); both must be exactly the single unset constraint` | **PASS** | — |
| 2 | `stale save threw SurrealVersionConflictException (expected version 1, DB had moved on)` | **PASS** | — |
| 3 | `bulk-saved 3 constraints under one fresh parent in one tx; reload sees 3` | **PASS** | — |
| 4 | `both saves carry id assesses:3bf08ca6803313a7d88dfa1c (ids match=True); after the null re-save the row's note IS NONE = True` | **PASS** | — |
| 5 | `Notes.Contains("smoke")→1 row(s); SELECT did not error over the mixed set/unset rows and returned only the set row` | **PASS** | — |
| 6 | `string::matches("seed-[0-9]+$")→10 row(s); expected the 10 seeded constraints only` | **PASS** | — |
| 7 | `created unchanged, updated advanced, version 1→2` | **PASS** | — |

Result line: `release smoke: all 7 checks PASSED`.

## 2. Item-2b — exception ordering (version guard vs. MVCC conflict)

Observed: `smoke[2b] NOTE (two open transactions) — surfaced: SurrealConflictException
(substrate MVCC, at COMMIT)`.

The two conflict-detection layers fire under different interleavings, and item 2 vs. 2b
demonstrate both:

- **Item 2 (stale snapshot, sequential):** a read-mode snapshot is captured, a *separate*
  session bumps the row's version and commits, then the stale entity is saved. The
  library's `[Version]`-guarded `UPDATE … WHERE version = $_expected_version` matches no
  row, so **`SurrealVersionConflictException` surfaces first — at `SaveAsync`, before
  COMMIT** (the library layer wins because the DB had already moved the version on).
- **Item 2b (two transactions open concurrently):** `txA` and `txB` both load the same
  design (same version) *before* either writes, so each guarded UPDATE sees a matching
  version and passes the library check. The conflict is only realized when the second
  COMMIT lands on a row the first COMMIT already touched, so **`SurrealConflictException`
  surfaces — from the substrate's MVCC, at COMMIT** (the version guard never fires
  because both snapshots predate both writes).

Ordering conclusion: the library version guard is the *first* line of defense whenever a
writer holds a snapshot that is already stale at save time; the substrate's MVCC is the
*backstop* for genuinely concurrent open transactions whose snapshots were both fresh at
read time. Both paths fail closed with a distinct, catchable exception type.

## 3. Identifier-quoting probe (§2)

> **Correction (2026-07-03, post-parser-source review).** The classification and
> recommendation below were revised after checking SurrealDB's own parser source. The
> authoritative reserved-word set is the 44-word `RESERVED_KEYWORD` `phf::Set` in
> `crates/core/src/syn/lexer/keywords.rs` @ tag `v3.1.4` — the exact set SurrealDB's own
> `EscapeIdent` serializer backtick-quotes. `order`, `group`, `type`, `count`, `limit`,
> `start`, `set`, `content`, `fetch`, `split`, and `default` are **not** in that set. The
> probes below only ever exercised *backtick-quoted* forms of `order`/`group`/`value`
> (Appendix B, session B1); no unquoted `order`/`group` control was run. Showing that the
> quoted forms *work* does not show the bare forms *need* quoting — that inference was
> over-broad, and the parser source disproves it for everything except `value`. Correct
> two-tier split (now shipped as **CG058**/**CG059** — see `docs/notes.md`):
> **hard/error** = the 4 value literals `none`/`null`/`true`/`false` (silent misparse —
> SurrealQL reads them as the literal, not an identifier, so no quoting rescues them);
> **soft/warning** = the other 40 `RESERVED_KEYWORD` words, including `value`, `select`,
> `where`, `table` (loud failure — parse/apply error, in principle rescuable by quoting,
> which is why they're a warning and not an error). The transcript and per-position
> observations below are unchanged and remain valid raw evidence; only the "order/group/
> value are all soft-reserved and need quoting" gloss was wrong — of that assumed trio,
> only `value` actually is.

### Per-position verdict (verbatim `quoting[label]` lines)

> Note: the "Probe statement" column reproduces each probe's SQL from the harness
> source (`Program.cs`, `ProbeIdentifierQuoting`); the harness prints only the label and
> verdict. Only the "Observed" column is printed output.

| Emit position | Probe statement (backtick-quoted) | Observed |
| --- | --- | --- |
| `define-table` | `DEFINE TABLE … quote_probe SCHEMAFULL` | **OK** |
| `define-field-order` | `DEFINE FIELD … `order` ON quote_probe TYPE string` | **OK** |
| `define-field-none` | `DEFINE FIELD … `none` ON quote_probe TYPE option<string>` | **OK** |
| `define-index` | `DEFINE INDEX … COLUMNS `order`` | **OK** |
| `create-set` | `CREATE quote_probe:one SET `order` = 'a', `none` = 'x'` | **FAILED** — `A user generated conversion error occured: Conversion("Expected an idiom")` |
| `where-orderby` | `SELECT * FROM quote_probe WHERE `order` = 'a' ORDER BY `order` ASC` | **FAILED** — `Failed to get field definitions` |
| `update-set` | `UPDATE quote_probe:one SET `none` = 'y'` | **FAILED** — `A user generated conversion error occured: Conversion("Expected an idiom")` |
| `subselect-alias` | `SELECT *, (SELECT id FROM quote_probe) AS `group` FROM quote_probe` | **FAILED** — `Failed to get field definitions` |

### Control probe — observed behavior (transcribed, not judged)

`quoting[control-unquoted-none] CONTROL — errored: Failed to get field definitions`.

The control (unquoted reserved word `none` in a predicate: `SELECT * FROM quote_probe
WHERE none = 'x'`) was expected by the harness comment to be *accepted and match 0 rows*
(bare `none` parses as the NONE literal → always-false). Instead it **errored** —
because it ran against `quote_probe`, whose `DEFINE FIELD `none`` had already poisoned
all reads of that table (see below). On a *clean* table the predicted always-false
behavior does hold (verified directly — Appendix B, B3.6).

### 3.1 Isolation — what actually failed, and why

The harness runs all probes against one `quote_probe` table that has **both** an `order`
column and a `none` column. That conflates two independent effects. Direct `surreal sql`
isolation sessions against fresh tables (each with a single column) untangle them; the
raw transcript — every statement and its actual response, from a fresh ephemeral
in-memory server — is **Appendix B** below, and every claim here cites its lines:

- **Backtick quoting is accepted in *every* emit position for "soft" reserved words.**
  `` `order` ``, `` `group` ``, and `` `value` `` each round-trip cleanly through
  DEFINE FIELD, DEFINE INDEX, CREATE … SET, WHERE, ORDER BY, UPDATE … SET, and
  subselect alias on a clean table — rows come back (Appendix B: B1.1–B1.7 for
  `order`, B1.8–B1.13 for `group`, B1.14–B1.19 for `value`; the subselect alias
  `AS `group`` is B1.7). The `create-set`/`where-orderby`/`subselect-alias` FAILEDs
  above are **not** a rejection of the `order`/`group` backticks.
  > **Correction:** only `value` is actually in SurrealDB's `RESERVED_KEYWORD` set
  > (v3.1.4, `keywords.rs`); `order`/`group` are not reserved at all. The round-trip
  > above is a real observation — backtick-quoting is harmless — but it does not mean
  > `order`/`group` needed quoting: no bare/unquoted control for `order`/`group` was
  > ever run (only the `none` control, B3.6), so this evidence never actually tested
  > whether the bare forms fail.
- **The failures are caused by the `none` column.** `none`, `null`, `true`, `false`
  (SurrealQL *value literals*) and `select` (a statement keyword) cannot be rescued by
  backticks in DML:
  - `CREATE … SET `none` = 'x'` → `Expected an idiom` even though the name is
    backtick-quoted (B2.3; same failure for `null`/`true`/`false` at B2.7/B2.11/B2.15,
    and a syntax error for `select` at B2.19);
  - and, more dangerously, **`DEFINE FIELD `none` …` is *accepted* at define time
    (B2.2) but silently poisons the table** — even a plain `SELECT * FROM …` with no
    WHERE clause then errors with `Failed to get field definitions` (B2.4; same for
    the other four at B2.8/B2.12/B2.16/B2.20). That is why `where-orderby`,
    `subselect-alias`, and the control (all SELECTs against `quote_probe`) failed even
    though `order`/`group` themselves quote fine — B3.1–B3.5 reproduce the harness's
    exact mixed-table shape: the two DEFINEs succeed (B3.2–B3.3), the combined
    `SET `order` = 'a', `none` = 'x'` fails `Expected an idiom` (B3.4), and a
    `WHERE `order``-only SELECT against that table fails
    `Failed to get field definitions` (B3.5) purely because the `none` field exists.
- **The underlying `none` silent-bug (Improvements.md item 1) is confirmed.** On a clean
  table with no `none` column, `SELECT * FROM iso_order WHERE none = 'x'` is accepted
  and returns `[]` — matches 0 rows, always-false (B3.6) — while the backtick-quoted
  `WHERE `order` = 'b'` returns the row (B3.7). So bare `none` really is an
  always-false predicate — the exact hazard the quoting work exists to remove.

### Recommendation

**Superseded 2026-07-03 (post-parser-source review).** The original call below inferred a
hybrid (quote everywhere reserved-adjacent *and* add a diagnostic) from the probe
evidence; the correction at the top of §3 shows that inference over-read what "backticks
are accepted" proves. Current, shipped decision:

- **Reject-only, two-tier, shipped.** **CG058 (error)** on the 4 value literals
  (`none`/`null`/`true`/`false`) — these misparse *silently* and no quoting can rescue
  them. **CG059 (warning)** on the other 40 words in SurrealDB's `RESERVED_KEYWORD` set
  (`keywords.rs` @ v3.1.4), including `value` and `select` — these fail *loudly* (a parse
  or apply error), which quoting *could* in principle rescue, but a warning is the shipped
  treatment: the generator flags them, the author renames or accepts the risk, no wire
  behavior changes. `order`/`group`/`type`/`count`/`limit`/`start` etc. are correctly
  *not* flagged — they are not in `RESERVED_KEYWORD`. See `docs/notes.md` (unreleased
  CG058/CG059 entry) for the implementation.
- **Backtick-quoting is deferred and optional**, not a follow-up PR already scoped to
  ship. If it's ever picked up, it must: (a) escape iff-in-`RESERVED_KEYWORD`, mirroring
  SurrealDB's own `EscapeIdent` serializer; (b) never ship without CG058 already in
  place — quoting a value-literal name would turn today's loud failure into a
  silently-accepted wrong query, which is strictly worse; (c) be justified first by a
  live test of a *bare* (unquoted) soft-reserved word actually failing in the position
  it's meant to fix — every probe in this report only ever ran backtick-quoted forms of
  `order`/`group`/`value`, so it never established that the bare forms break.

<details>
<summary>Original 2026-07-03 recommendation (superseded by the correction above, kept for history)</summary>

The pure "backticks accepted everywhere → just quote at the chokepoint" outcome did **not**
hold, and the pure "any rejection → abandon quoting for a diagnostic" outcome is too
blunt. The evidence points to a **hybrid**, and the owner call (remaining-work §4 Q1)
should be made on that basis:

1. **Implement backtick-quoting at the `SurrealFormatter.Identifier()` chokepoint plus
   the four codegen-baked emitters** (DEFINE FIELD/INDEX columns in `SchemaEmitter`,
   CREATE/UPDATE SET, WHERE/ORDER BY, subselect aliases). This is proven to rescue the
   large class of soft-reserved identifiers — `order`, `group`, `value`, and the great
   majority of SurrealQL keywords — in *every* emit position. It is worth doing and is
   the immediate follow-up PR.
2. **Add a reserved-word generator diagnostic that rejects the small unrescuable set**
   — the value literals `none`, `null`, `true`, `false` and the statement keyword
   `select` — as `[Property]`/column names at generate time. Quoting cannot save these
   (SET fails; and a bare `DEFINE FIELD` on such a name silently breaks reads), so a
   fail-closed diagnostic is the only safe treatment. This is the "alternative if quoting
   misbehaves anywhere" from review §5, scoped to exactly the identifiers where it does.

Quoting alone would give a false sense of safety for `none`-family columns (schema
applies, reads then break at runtime); a diagnostic alone would needlessly reject
`order`/`group`/`value`, which quoting handles perfectly. Do both.

*(This "order`/`group`/`value` all soft-reserved, quote them" framing is exactly the
over-broad inference the parser source corrected — `order`/`group` were never reserved.)*

</details>

## Appendix A — raw harness smoke/probe output

```
--- Release smoke (remaining-work.md §1 live shapes) ---
  smoke[1] PASS — IsNone→1 row(s), Eq(null)→1 row(s); both must be exactly the single unset constraint
  smoke[2] PASS — stale save threw SurrealVersionConflictException (expected version 1, DB had moved on)
  smoke[2b] NOTE (two open transactions) — surfaced: SurrealConflictException (substrate MVCC, at COMMIT)
  smoke[3] PASS — bulk-saved 3 constraints under one fresh parent in one tx; reload sees 3
  smoke[4] PASS — both saves carry id assesses:3bf08ca6803313a7d88dfa1c (ids match=True); after the null re-save the row's note IS NONE = True
  smoke[5] PASS — Notes.Contains("smoke")→1 row(s); SELECT did not error over the mixed set/unset rows and returned only the set row
  smoke[6] PASS — string::matches("seed-[0-9]+$")→10 row(s); expected the 10 seeded constraints only
  smoke[7] PASS — created unchanged, updated advanced, version 1→2
  release smoke: all 7 checks PASSED

--- Identifier-quoting probe (remaining-work.md §2) ---
  quoting[define-table] OK
  quoting[define-field-order] OK
  quoting[define-field-none] OK
  quoting[define-index] OK
  quoting[create-set] FAILED — A user generated conversion error occured: Conversion("Expected an idiom")
  quoting[where-orderby] FAILED — Failed to get field definitions
  quoting[update-set] FAILED — A user generated conversion error occured: Conversion("Expected an idiom")
  quoting[subselect-alias] FAILED — Failed to get field definitions
  quoting[control-unquoted-none] CONTROL — errored: Failed to get field definitions
```

## Appendix B — raw CLI isolation transcript

Captured 2026-07-03 against a **fresh** ephemeral in-memory server (same recipe:
`surreal start --bind 127.0.0.1:8000 --default-namespace project-brain
--default-database workspace --username root --password secret`, no path arg; killed
after the run). Three sessions, each a script piped to
`surreal sql --endpoint http://127.0.0.1:8000 --username root --password secret
--namespace project-brain --database workspace --hide-welcome`. The CLI prints one
response per statement, in statement order; each `Bn.m` entry below pairs a statement
with its actual response by that order. Statements and responses are verbatim.

### Session B1 — soft reserved words on clean single-column tables

```
B1.1   > DEFINE TABLE IF NOT EXISTS iso_order SCHEMAFULL;
       [NONE]
B1.2   > DEFINE FIELD IF NOT EXISTS `order` ON iso_order TYPE string;
       [NONE]
B1.3   > DEFINE INDEX IF NOT EXISTS idx_iso_order ON TABLE iso_order COLUMNS `order`;
       [NONE]
B1.4   > CREATE iso_order:one SET `order` = 'a';
       [[{ id: iso_order:one, order: 'a' }]]
B1.5   > SELECT * FROM iso_order WHERE `order` = 'a' ORDER BY `order` ASC;
       [[{ id: iso_order:one, order: 'a' }]]
B1.6   > UPDATE iso_order:one SET `order` = 'b';
       [[{ id: iso_order:one, order: 'b' }]]
B1.7   > SELECT *, (SELECT id FROM iso_order) AS `group` FROM iso_order;
       [[{ group: [{ id: iso_order:one }], id: iso_order:one, order: 'b' }]]
B1.8   > DEFINE TABLE IF NOT EXISTS iso_group SCHEMAFULL;
       [NONE]
B1.9   > DEFINE FIELD IF NOT EXISTS `group` ON iso_group TYPE string;
       [NONE]
B1.10  > DEFINE INDEX IF NOT EXISTS idx_iso_group ON TABLE iso_group COLUMNS `group`;
       [NONE]
B1.11  > CREATE iso_group:one SET `group` = 'a';
       [[{ group: 'a', id: iso_group:one }]]
B1.12  > SELECT * FROM iso_group WHERE `group` = 'a' ORDER BY `group` ASC;
       [[{ group: 'a', id: iso_group:one }]]
B1.13  > UPDATE iso_group:one SET `group` = 'b';
       [[{ group: 'b', id: iso_group:one }]]
B1.14  > DEFINE TABLE IF NOT EXISTS iso_value SCHEMAFULL;
       [NONE]
B1.15  > DEFINE FIELD IF NOT EXISTS `value` ON iso_value TYPE string;
       [NONE]
B1.16  > DEFINE INDEX IF NOT EXISTS idx_iso_value ON TABLE iso_value COLUMNS `value`;
       [NONE]
B1.17  > CREATE iso_value:one SET `value` = 'a';
       [[{ id: iso_value:one, value: 'a' }]]
B1.18  > SELECT * FROM iso_value WHERE `value` = 'a' ORDER BY `value` ASC;
       [[{ id: iso_value:one, value: 'a' }]]
B1.19  > UPDATE iso_value:one SET `value` = 'b';
       [[{ id: iso_value:one, value: 'b' }]]
```

### Session B2 — value literals + `select`, each on its own fresh table

```
B2.1   > DEFINE TABLE IF NOT EXISTS bad_none SCHEMAFULL;
       [NONE]
B2.2   > DEFINE FIELD IF NOT EXISTS `none` ON bad_none TYPE option<string>;
       [NONE]
B2.3   > CREATE bad_none:one SET `none` = 'x';
       ['A user generated conversion error occured: Conversion("Expected an idiom")']
B2.4   > SELECT * FROM bad_none;
       ['Failed to get field definitions']
B2.5   > DEFINE TABLE IF NOT EXISTS bad_null SCHEMAFULL;
       [NONE]
B2.6   > DEFINE FIELD IF NOT EXISTS `null` ON bad_null TYPE string;
       [NONE]
B2.7   > CREATE bad_null:one SET `null` = 'x';
       ['A user generated conversion error occured: Conversion("Expected an idiom")']
B2.8   > SELECT * FROM bad_null;
       ['Failed to get field definitions']
B2.9   > DEFINE TABLE IF NOT EXISTS bad_true SCHEMAFULL;
       [NONE]
B2.10  > DEFINE FIELD IF NOT EXISTS `true` ON bad_true TYPE string;
       [NONE]
B2.11  > CREATE bad_true:one SET `true` = 'x';
       ['A user generated conversion error occured: Conversion("Expected an idiom")']
B2.12  > SELECT * FROM bad_true;
       ['Failed to get field definitions']
B2.13  > DEFINE TABLE IF NOT EXISTS bad_false SCHEMAFULL;
       [NONE]
B2.14  > DEFINE FIELD IF NOT EXISTS `false` ON bad_false TYPE string;
       [NONE]
B2.15  > CREATE bad_false:one SET `false` = 'x';
       ['A user generated conversion error occured: Conversion("Expected an idiom")']
B2.16  > SELECT * FROM bad_false;
       ['Failed to get field definitions']
B2.17  > DEFINE TABLE IF NOT EXISTS bad_select SCHEMAFULL;
       [NONE]
B2.18  > DEFINE FIELD IF NOT EXISTS `select` ON bad_select TYPE string;
       [NONE]
B2.19  > CREATE bad_select:one SET `select` = 'x';
       ['A user generated conversion error occured: Conversion("SyntaxError { diagnostic: Diagnostic { kind: Span { kind: Error, span: Span { offset: 6, len: 0 }, label: None }, next: Some(Diagnostic { kind: Cause(\\"Unexpected end of file, expected an expression\\"), next: None }) } }")']
B2.20  > SELECT * FROM bad_select;
       ['Failed to get field definitions']
```

### Session B3 — harness-confound reproduction + bare-`none` control

`mix_probe` mirrors the harness's `quote_probe` (both an `order` and a `none` column);
B3.6–B3.7 run against the clean `iso_order` table from session B1 (whose row holds
`order = 'b'` after B1.6).

```
B3.1   > DEFINE TABLE IF NOT EXISTS mix_probe SCHEMAFULL;
       [NONE]
B3.2   > DEFINE FIELD IF NOT EXISTS `order` ON mix_probe TYPE string;
       [NONE]
B3.3   > DEFINE FIELD IF NOT EXISTS `none` ON mix_probe TYPE option<string>;
       [NONE]
B3.4   > CREATE mix_probe:one SET `order` = 'a', `none` = 'x';
       ['A user generated conversion error occured: Conversion("Expected an idiom")']
B3.5   > SELECT * FROM mix_probe WHERE `order` = 'a';
       ['Failed to get field definitions']
B3.6   > SELECT * FROM iso_order WHERE none = 'x';
       [[]]
B3.7   > SELECT * FROM iso_order WHERE `order` = 'b';
       [[{ id: iso_order:one, order: 'b' }]]
```
