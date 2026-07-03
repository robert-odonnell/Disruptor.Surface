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

### Per-position verdict (verbatim `quoting[label]` lines)

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
behavior does hold (verified directly — see §3.1).

### 3.1 Isolation — what actually failed, and why

The harness runs all probes against one `quote_probe` table that has **both** an `order`
column and a `none` column. That conflates two independent effects. Direct `surreal sql`
isolation runs against fresh tables (each with a single column) untangle them:

- **Backtick quoting is accepted in *every* emit position for "soft" reserved words.**
  `` `order` ``, `` `group` ``, and `` `value` `` each round-trip cleanly through
  DEFINE FIELD, DEFINE INDEX, CREATE … SET, WHERE, ORDER BY, and subselect alias on a
  clean table — rows come back. The `create-set`/`where-orderby`/`subselect-alias`
  FAILEDs above are **not** a rejection of the `order`/`group` backticks.
- **The failures are caused by the `none` column.** `none`, `null`, `true`, `false`
  (SurrealQL *value literals*) and `select` (a statement keyword) cannot be rescued by
  backticks in DML:
  - `CREATE … SET `none` = 'x'` → `Expected an idiom` (the quoted name still parses as
    the value literal, which is not a valid assignment target);
  - and, more dangerously, **`DEFINE FIELD `none` …` is *accepted* at define time but
    silently poisons the table** — every subsequent `SELECT` against it errors with
    `Failed to get field definitions`. That is why `where-orderby`, `subselect-alias`,
    and the control (all SELECTs against `quote_probe`) failed even though `order`/`group`
    themselves quote fine.
- **The underlying `none` silent-bug (Improvements.md item 1) is confirmed.** On a clean
  table with no `none` column, `SELECT … WHERE none = 'a'` returns `[]` (matches 0 rows,
  always-false), while `SELECT … WHERE `order` = 'a'` returns the row. So bare `none`
  really is an always-false predicate — the exact hazard the quoting work exists to
  remove.

### Recommendation

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

## Appendix — raw harness smoke/probe output

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
